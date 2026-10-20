// 文件路径上传编排:不超过阈值走单次上传,超过的自动分片(算整文件 SHA-256 → init → 并发传片 → complete)。
// 与 chunkUpload.ts 是两条互不相干的路:那边对应 sys_file 的断点续传和秒传,这边按文件路径编码落到配置好的文件夹,没有这两项。
// 分片模式在同一次上传之内自愈:分片和合并遇到网络错误、超时、5xx、文件夹暂时访问不了时退避重试,合并报缺片时补传那一片再合并一次。
import { ApiError, filePathFileApi, type FilePathUploadOutput } from '#/api'

/** 超过这个大小自动分片;正好等于时仍走单次上传。 */
export const FILE_PATH_CHUNK_THRESHOLD = 20 * 1024 * 1024
/** 并发上传的分片数。 */
const CONCURRENCY = 3
/** 重试前的等待(毫秒):第 n 次重试前等第 n 项,用完就放弃。 */
const RETRY_DELAYS_MS = [1000, 2000, 4000]
/** 文件夹无法访问(服务器暂时不通、繁忙):服务端保留分片会话,稍后再试可能成功。 */
const FOLDER_UNAVAILABLE = 44114
/** 合并时缺分片,args.index 是缺的那一片。 */
const CHUNK_MISSING = 44115

/**
 * 算整文件 SHA-256(hex,小写),服务端合并后重算比对。
 * shortcut: 一次性读入内存做全量 hash,正确优先;超大文件想省内存要改分块流式 hash,需引库,当前不引依赖。
 */
async function sha256Hex(file: File): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', await file.arrayBuffer())
  return [...new Uint8Array(digest)].map(b => b.toString(16).padStart(2, '0')).join('')
}

/**
 * 再试一次可能成功的失败:fetch 连不上(TypeError)、客户端超时闸到点(TimeoutError)、5xx、文件夹暂时访问不了。
 * 业务校验类错误(会话无效、哈希不符、规则不允许)再试结果也一样,不在其列;中止(AbortError)也不重试。
 */
function isTransient(e: unknown): boolean {
  if (e instanceof ApiError) return e.code === FOLDER_UNAVAILABLE || (e.code >= 500 && e.code < 600)
  return e instanceof TypeError || (e as { name?: unknown } | null)?.name === 'TimeoutError'
}

/** 等 ms 毫秒;signal 中止时立即以它的原因失败。 */
function sleep(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal.aborted) {
      reject(signal.reason)
      return
    }
    const onAbort = () => {
      clearTimeout(timer)
      reject(signal.reason)
    }
    const timer = setTimeout(() => {
      signal.removeEventListener('abort', onAbort)
      resolve()
    }, ms)
    signal.addEventListener('abort', onAbort, { once: true })
  })
}

/** 按 RETRY_DELAYS_MS 退避重试暂时性失败;signal 中止后不再重试。 */
async function withRetry<T>(op: () => Promise<T>, signal: AbortSignal): Promise<T> {
  for (let attempt = 0; ; attempt++) {
    try {
      return await op()
    } catch (e) {
      if (signal.aborted || attempt >= RETRY_DELAYS_MS.length || !isTransient(e)) throw e
    }
    await sleep(RETRY_DELAYS_MS[attempt], signal)
  }
}

/**
 * 把文件传进编码对应的文件路径,返回相对文件夹的路径(业务存它,之后凭它下载)。
 * @param opts.subPath 仅「业务指定路径」命名规则使用,其余规则服务端忽略。
 * @param opts.onProgress 进度回调(0–100)。仅分片上传会报:传片阶段最多到 99,服务端合并完成后才报 100。
 * @param opts.signal 中止这次上传(组件卸载等):在途的请求一并取消,不再重试。
 */
export async function uploadToFilePath(
  code: string,
  file: File,
  opts: { subPath?: string; onProgress?: (percent: number) => void; signal?: AbortSignal } = {},
): Promise<FilePathUploadOutput> {
  const { subPath, onProgress, signal } = opts
  if (file.size <= FILE_PATH_CHUNK_THRESHOLD) {
    return filePathFileApi.upload(code, file, subPath, signal)
  }

  const sha256 = await sha256Hex(file)
  // 分片大小以服务端为准:它按自己的配置算出片数,客户端照切,最后一片可以短。
  const { uploadId, chunkSize, chunkCount } = await filePathFileApi.chunkInit(
    code,
    { fileName: file.name, size: file.size, sha256, subPath },
    signal,
  )

  // 本次上传自己的中止:任一片最终失败就取消其余在途的分片;调用方的 signal 一并生效
  const stop = new AbortController()
  const abort = signal ? AbortSignal.any([signal, stop.signal]) : stop.signal
  const sendChunk = (i: number) =>
    withRetry(
      () =>
        filePathFileApi.chunk(
          code,
          uploadId,
          i,
          file.slice(i * chunkSize, (i + 1) * chunkSize),
          abort,
        ),
      abort,
    )

  let uploaded = 0
  onProgress?.(0)
  // 限并发的 worker 池:共享游标,各自领片上传;任一片失败后其余 worker 不再领新片,在途的也取消,免得出错后还在往服务端灌数据。
  let next = 0
  let failure: { error: unknown } | undefined
  async function worker() {
    while (!failure && next < chunkCount) {
      const i = next++
      try {
        await sendChunk(i)
      } catch (e) {
        // 被取消的分片也走到这里:一律抛最先失败的那一片自己的错误,不抛取消引出的 AbortError
        failure ??= { error: e }
        stop.abort()
        throw failure.error
      }
      // 别的分片已经失败:这片即使传完也不再计数、不报进度,否则 n-upload 会把已失败的文件改回「上传中」
      if (failure) return
      uploaded++
      onProgress?.(Math.min(99, Math.round((uploaded / chunkCount) * 100)))
    }
  }
  await Promise.all(Array.from({ length: Math.min(CONCURRENCY, chunkCount) }, worker))

  const complete = () =>
    withRetry(() => filePathFileApi.chunkComplete(code, uploadId, abort), abort)
  let out: FilePathUploadOutput
  try {
    out = await complete()
  } catch (e) {
    // 缺片(分片落在了没共享暂存目录的副本上、被清扫过):补传缺的那一片再合并一次,只补这一轮
    const index = e instanceof ApiError && e.code === CHUNK_MISSING ? e.args?.index : undefined
    if (typeof index !== 'number') throw e
    await sendChunk(index)
    out = await complete()
  }
  onProgress?.(100)
  return out
}
