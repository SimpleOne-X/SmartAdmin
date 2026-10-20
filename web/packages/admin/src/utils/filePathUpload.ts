// 文件路径上传编排:不超过阈值走单次上传,超过的自动分片(算整文件 SHA-256 → init → 并发传片 → complete)。
// 与 chunkUpload.ts 是两条互不相干的路:那边对应 sys_file 的断点续传和秒传,这边按文件路径编码落到配置好的文件夹,没有这两项。
import { filePathFileApi, type FilePathUploadOutput } from '#/api'

/** 超过这个大小自动分片;正好等于时仍走单次上传。 */
export const FILE_PATH_CHUNK_THRESHOLD = 20 * 1024 * 1024
/** 并发上传的分片数。 */
const CONCURRENCY = 3

/**
 * 算整文件 SHA-256(hex,小写),服务端合并后重算比对。
 * shortcut: 一次性读入内存做全量 hash,正确优先;超大文件想省内存要改分块流式 hash,需引库,当前不引依赖。
 */
async function sha256Hex(file: File): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', await file.arrayBuffer())
  return [...new Uint8Array(digest)].map(b => b.toString(16).padStart(2, '0')).join('')
}

/**
 * 把文件传进编码对应的文件路径,返回相对文件夹的路径(业务存它,之后凭它下载)。
 * @param opts.subPath 仅「业务指定路径」命名规则使用,其余规则服务端忽略。
 * @param opts.onProgress 进度回调(0–100)。仅分片上传会报:传片阶段最多到 99,服务端合并完成后才报 100。
 */
export async function uploadToFilePath(
  code: string,
  file: File,
  opts: { subPath?: string; onProgress?: (percent: number) => void } = {},
): Promise<FilePathUploadOutput> {
  const { subPath, onProgress } = opts
  if (file.size <= FILE_PATH_CHUNK_THRESHOLD) {
    return filePathFileApi.upload(code, file, subPath)
  }

  const sha256 = await sha256Hex(file)
  // 分片大小以服务端为准:它按自己的配置算出片数,客户端照切,最后一片可以短。
  const { uploadId, chunkSize, chunkCount } = await filePathFileApi.chunkInit(code, {
    fileName: file.name,
    size: file.size,
    sha256,
    subPath,
  })

  let uploaded = 0
  onProgress?.(0)
  // 限并发的 worker 池:共享游标,各自领片上传;任一片失败后其余 worker 不再领新片,免得出错后还在往服务端灌数据。
  let next = 0
  let failed = false
  async function worker() {
    while (!failed && next < chunkCount) {
      const i = next++
      try {
        await filePathFileApi.chunk(
          code,
          uploadId,
          i,
          file.slice(i * chunkSize, (i + 1) * chunkSize),
        )
      } catch (e) {
        failed = true
        throw e
      }
      // 别的分片已经失败:这片即使传完也不再计数、不报进度,否则 n-upload 会把已失败的文件改回「上传中」
      if (failed) return
      uploaded++
      onProgress?.(Math.min(99, Math.round((uploaded / chunkCount) * 100)))
    }
  }
  await Promise.all(Array.from({ length: Math.min(CONCURRENCY, chunkCount) }, worker))

  const out = await filePathFileApi.chunkComplete(code, uploadId)
  onProgress?.(100)
  return out
}
