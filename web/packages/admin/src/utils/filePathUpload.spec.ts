import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// 上传端点由 filePathFileApi 提供,这里只验编排:走单次还是分片、分片怎么切、进度怎么报、失败怎么重试。
const api = vi.hoisted(() => {
  // 与 #/api 的 ApiError 同形:业务码、msgKey、args
  class ApiError extends Error {
    code: number
    args?: Record<string, unknown>
    constructor(code: number, msgKey?: string, args?: Record<string, unknown>) {
      super(msgKey ?? `Error ${code}`)
      this.name = 'ApiError'
      this.code = code
      this.args = args
    }
  }
  return {
    ApiError,
    filePathFileApi: {
      upload: vi.fn(),
      chunkInit: vi.fn(),
      chunk: vi.fn(),
      chunkComplete: vi.fn(),
    },
  }
})
vi.mock('#/api', () => api)

import { FILE_PATH_CHUNK_THRESHOLD, uploadToFilePath } from './filePathUpload'

const { upload, chunkInit, chunk, chunkComplete } = api.filePathFileApi

const output = { path: '2026/10/a.bin', name: 'a.bin', size: 1, viewUrl: null }

const fileOfSize = (size: number) => new File([new Uint8Array(size)], 'a.bin')

/** 每个字节 = 所在位置 % 251:251 与分片大小互质,各片起点的首字节互不相同,切错偏移一眼能认出来。 */
const patternByte = (pos: number) => pos % 251
function patternedFile(size: number) {
  const bytes = new Uint8Array(size)
  for (let i = 0; i < size; i++) bytes[i] = patternByte(i)
  return new File([bytes], 'a.bin')
}

beforeEach(() => {
  vi.clearAllMocks()
  upload.mockResolvedValue(output)
  chunkComplete.mockResolvedValue(output)
  chunk.mockResolvedValue(undefined)
})

/** 服务端按文件大小均分成 count 片(最后一片可以短)。 */
function initFor(size: number, count: number) {
  chunkInit.mockResolvedValue({
    uploadId: 'u1',
    chunkSize: Math.ceil(size / count),
    chunkCount: count,
  })
}

describe('uploadToFilePath', () => {
  it('uses_single_upload_at_threshold', async () => {
    const file = fileOfSize(FILE_PATH_CHUNK_THRESHOLD)

    const out = await uploadToFilePath('Order.Attachment', file)

    expect(out).toBe(output)
    expect(upload).toHaveBeenCalledTimes(1)
    expect(upload).toHaveBeenCalledWith('Order.Attachment', file, undefined, undefined)
    expect(chunkInit).not.toHaveBeenCalled()
  })

  it('uses_chunks_above_threshold', async () => {
    const file = patternedFile(FILE_PATH_CHUNK_THRESHOLD + 1)
    initFor(file.size, 5)
    const order: string[] = []
    chunkComplete.mockImplementation(async () => {
      order.push('complete')
      return output
    })
    const progress: number[] = []

    const out = await uploadToFilePath('Order.Attachment', file, {
      onProgress: p => {
        order.push(`progress:${p}`)
        progress.push(p)
      },
    })

    expect(out).toBe(output)
    expect(upload).not.toHaveBeenCalled()
    expect(chunkInit).toHaveBeenCalledWith(
      'Order.Attachment',
      {
        fileName: 'a.bin',
        size: file.size,
        sha256: expect.stringMatching(/^[0-9a-f]{64}$/),
        subPath: undefined,
      },
      undefined,
    )
    expect(chunk).toHaveBeenCalledTimes(5)
    const indexes = chunk.mock.calls.map(c => c[2]).toSorted()
    expect(indexes).toEqual([0, 1, 2, 3, 4])
    expect(chunk.mock.calls.every(c => c[0] === 'Order.Attachment' && c[1] === 'u1')).toBe(true)
    // 按服务端给的 chunkSize 切:各片加起来正好是整个文件
    const total = chunk.mock.calls.reduce((sum, c) => sum + (c[3] as Blob).size, 0)
    expect(total).toBe(file.size)
    // 每片从自己的偏移切起:首尾字节对得上原文件对应位置
    const chunkSize = Math.ceil(file.size / 5)
    for (const [, , index, blob] of chunk.mock.calls as [string, string, number, Blob][]) {
      const start = index * chunkSize
      const end = Math.min(start + chunkSize, file.size)
      const bytes = new Uint8Array(await blob.arrayBuffer())
      expect(bytes.length).toBe(end - start)
      expect(bytes[0]).toBe(patternByte(start))
      expect(bytes.at(-1)).toBe(patternByte(end - 1))
    }
    expect(chunkComplete).toHaveBeenCalledWith('Order.Attachment', 'u1', expect.any(AbortSignal))

    // complete 之前最多报 99,100 只在 complete 之后出现且是最后一次
    expect(progress.at(-1)).toBe(100)
    expect(order.indexOf('progress:100')).toBeGreaterThan(order.indexOf('complete'))
    expect(order.slice(0, order.indexOf('complete')).every(s => s !== 'progress:100')).toBe(true)
  })

  it('limits_chunk_concurrency_to_three', async () => {
    const file = fileOfSize(FILE_PATH_CHUNK_THRESHOLD + 1)
    initFor(file.size, 5)
    let inFlight = 0
    let peak = 0
    chunk.mockImplementation(async () => {
      peak = Math.max(peak, ++inFlight)
      await new Promise(r => setTimeout(r, 5))
      inFlight--
    })

    await uploadToFilePath('Order.Attachment', file)

    expect(peak).toBe(3)
  })

  it('stops_claiming_chunks_after_a_failure', async () => {
    const file = fileOfSize(FILE_PATH_CHUNK_THRESHOLD + 1)
    initFor(file.size, 5)
    chunk.mockRejectedValue(new Error('boom'))

    await expect(uploadToFilePath('Order.Attachment', file)).rejects.toThrow('boom')
    // 三个 worker 各领了一片,失败后不再领第 4、5 片,也不会去 complete
    expect(chunk).toHaveBeenCalledTimes(3)
    expect(chunkComplete).not.toHaveBeenCalled()
  })

  it('does_not_report_progress_after_a_failure', async () => {
    const file = fileOfSize(FILE_PATH_CHUNK_THRESHOLD + 1)
    initFor(file.size, 5)
    // 前三片同时在飞,谁先落定由用例控制
    const gates = Array.from({ length: 3 }, () => Promise.withResolvers<void>())
    chunk.mockImplementation((_code, _id, index: number) => gates[index].promise)
    const progress: number[] = []

    const run = uploadToFilePath('Order.Attachment', file, { onProgress: p => progress.push(p) })
    await vi.waitFor(() => expect(chunk).toHaveBeenCalledTimes(3))

    gates[0].reject(new Error('first'))
    await expect(run).rejects.toThrow('first')
    const reported = progress.length

    // 失败之后才完成的分片:不计数、不报进度(n-upload 收到进度会把已失败的文件改回「上传中」)
    gates[1].resolve()
    gates[2].resolve()
    await new Promise(r => setTimeout(r, 10))
    expect(progress.length).toBe(reported)
    expect(chunk).toHaveBeenCalledTimes(3)
  })

  it('passes_sub_path', async () => {
    const small = fileOfSize(10)
    await uploadToFilePath('Order.Attachment', small, { subPath: 'a/b' })
    expect(upload).toHaveBeenCalledWith('Order.Attachment', small, 'a/b', undefined)

    const big = fileOfSize(FILE_PATH_CHUNK_THRESHOLD + 1)
    initFor(big.size, 5)
    await uploadToFilePath('Order.Attachment', big, { subPath: 'a/b' })
    expect(chunkInit).toHaveBeenCalledWith(
      'Order.Attachment',
      expect.objectContaining({ subPath: 'a/b' }),
      undefined,
    )
  })
})

/** 挂住直到传给它的 signal 中止;中止时按 fetch 的样子抛 AbortError。 */
const hangUntilAborted = (signal: AbortSignal) =>
  new Promise<void>((_, reject) =>
    signal.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError'))),
  )

describe('uploadToFilePath 的重试、补传与中止', () => {
  // 只假 setTimeout:退避等待由用例推进;算哈希、读 Blob 照常异步完成
  const realSetTimeout = globalThis.setTimeout
  /** 让出真实的事件循环,等异步链走完(算哈希、mock 落定后的续延)。 */
  async function until(check: () => boolean) {
    for (let i = 0; i < 500 && !check(); i++) await new Promise(r => realSetTimeout(r, 2))
    expect(check()).toBe(true)
  }
  const settle = () => new Promise(r => realSetTimeout(r, 20))

  const { ApiError } = api
  const callsFor = (index: number) => chunk.mock.calls.filter(c => c[2] === index).length

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
  })
  afterEach(() => {
    vi.useRealTimers()
  })

  it('retries_a_failed_chunk_after_1_2_4_seconds_then_succeeds', async () => {
    const file = fileOfSize(FILE_PATH_CHUNK_THRESHOLD + 1)
    initFor(file.size, 1)
    chunk
      .mockRejectedValueOnce(new TypeError('Failed to fetch'))
      .mockRejectedValueOnce(
        new ApiError(44114, 'error.filePath.folderUnavailable', { reason: 'busy' }),
      )
      .mockRejectedValueOnce(new DOMException('timed out', 'TimeoutError'))
      .mockResolvedValue(undefined)

    const run = uploadToFilePath('Order.Attachment', file)
    await until(() => chunk.mock.calls.length === 1)

    await vi.advanceTimersByTimeAsync(999)
    await settle()
    expect(chunk).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(1)
    await until(() => chunk.mock.calls.length === 2)

    await vi.advanceTimersByTimeAsync(1999)
    await settle()
    expect(chunk).toHaveBeenCalledTimes(2)
    await vi.advanceTimersByTimeAsync(1)
    await until(() => chunk.mock.calls.length === 3)

    await vi.advanceTimersByTimeAsync(3999)
    await settle()
    expect(chunk).toHaveBeenCalledTimes(3)
    await vi.advanceTimersByTimeAsync(1)

    await expect(run).resolves.toBe(output)
    expect(chunk).toHaveBeenCalledTimes(4)
    expect(chunk.mock.calls.every(c => c[2] === 0)).toBe(true)
  })

  it('retries_5xx_but_not_business_errors', async () => {
    const file = fileOfSize(FILE_PATH_CHUNK_THRESHOLD + 1)
    initFor(file.size, 1)
    chunk.mockRejectedValueOnce(new ApiError(502)).mockResolvedValue(undefined)

    const ok = uploadToFilePath('Order.Attachment', file)
    await until(() => chunk.mock.calls.length === 1)
    await vi.advanceTimersByTimeAsync(1000)
    await expect(ok).resolves.toBe(output)
    expect(chunk).toHaveBeenCalledTimes(2)

    // 会话无效这类业务错误再传一次结果也一样,直接失败
    chunk.mockReset()
    const invalid = new ApiError(44117, 'error.filePath.uploadSessionInvalid')
    chunk.mockRejectedValue(invalid)
    await expect(uploadToFilePath('Order.Attachment', file)).rejects.toBe(invalid)
    await vi.advanceTimersByTimeAsync(10_000)
    expect(chunk).toHaveBeenCalledTimes(1)
  })

  it('gives_up_after_three_retries_fails_once_and_aborts_the_other_chunks', async () => {
    const file = fileOfSize(FILE_PATH_CHUNK_THRESHOLD + 1)
    initFor(file.size, 3)
    const offline = new TypeError('Failed to fetch')
    const signals: AbortSignal[] = []
    chunk.mockImplementation((_code, _id, index: number, _blob, signal: AbortSignal) => {
      if (index === 0) return Promise.reject(offline)
      signals.push(signal)
      return hangUntilAborted(signal)
    })
    const progress: number[] = []
    const failures: unknown[] = []

    const run = uploadToFilePath('Order.Attachment', file, {
      onProgress: p => progress.push(p),
    }).catch(e => failures.push(e))
    await until(() => chunk.mock.calls.length === 3)
    await vi.advanceTimersByTimeAsync(7000)
    await run

    // 失败一次,报的是分片自己的错误,不是随后中止其余分片引出的 AbortError
    expect(failures).toEqual([offline])
    expect(callsFor(0)).toBe(4)
    expect(signals).toHaveLength(2)
    expect(signals.every(s => s.aborted)).toBe(true)
    expect(chunkComplete).not.toHaveBeenCalled()

    // 中止的分片不重试,也不再领新片
    await vi.advanceTimersByTimeAsync(10_000)
    await settle()
    expect(chunk).toHaveBeenCalledTimes(6)
    expect(progress).toEqual([0])
  })

  it('reuploads_the_missing_chunk_and_completes_again_once', async () => {
    const file = fileOfSize(FILE_PATH_CHUNK_THRESHOLD + 1)
    initFor(file.size, 2)
    chunkComplete
      .mockRejectedValueOnce(new ApiError(44115, 'error.filePath.chunkMissing', { index: 1 }))
      .mockResolvedValue(output)

    await expect(uploadToFilePath('Order.Attachment', file)).resolves.toBe(output)

    expect(callsFor(0)).toBe(1)
    expect(callsFor(1)).toBe(2)
    // 补传的就是缺的那一片:长度与第一次一致
    const [first, again] = chunk.mock.calls.filter(c => c[2] === 1).map(c => c[3] as Blob)
    expect(again.size).toBe(first.size)
    expect(chunkComplete).toHaveBeenCalledTimes(2)
  })

  it('missing_chunk_twice_fails', async () => {
    const file = fileOfSize(FILE_PATH_CHUNK_THRESHOLD + 1)
    initFor(file.size, 2)
    const missing = new ApiError(44115, 'error.filePath.chunkMissing', { index: 0 })
    chunkComplete.mockRejectedValue(missing)

    await expect(uploadToFilePath('Order.Attachment', file)).rejects.toBe(missing)

    // 只补一轮:补一次、再合并一次
    expect(callsFor(0)).toBe(2)
    expect(chunkComplete).toHaveBeenCalledTimes(2)
  })

  it('retries_complete_while_the_folder_is_unavailable', async () => {
    const file = fileOfSize(FILE_PATH_CHUNK_THRESHOLD + 1)
    initFor(file.size, 2)
    chunkComplete
      .mockRejectedValueOnce(
        new ApiError(44114, 'error.filePath.folderUnavailable', { reason: 'timeout' }),
      )
      .mockResolvedValue(output)
    const progress: number[] = []

    const run = uploadToFilePath('Order.Attachment', file, { onProgress: p => progress.push(p) })
    await until(() => chunkComplete.mock.calls.length === 1)
    await vi.advanceTimersByTimeAsync(999)
    await settle()
    expect(chunkComplete).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(1)

    await expect(run).resolves.toBe(output)
    expect(chunkComplete).toHaveBeenCalledTimes(2)
    expect(chunk).toHaveBeenCalledTimes(2)
    expect(progress.at(-1)).toBe(100)
  })

  it('aborts_everything_when_the_caller_aborts', async () => {
    const file = fileOfSize(FILE_PATH_CHUNK_THRESHOLD + 1)
    initFor(file.size, 5)
    const signals: AbortSignal[] = []
    chunk.mockImplementation((_code, _id, _index, _blob, signal: AbortSignal) => {
      signals.push(signal)
      return hangUntilAborted(signal)
    })
    const caller = new AbortController()

    const run = uploadToFilePath('Order.Attachment', file, { signal: caller.signal })
    await until(() => chunk.mock.calls.length === 3)
    caller.abort()

    await expect(run).rejects.toMatchObject({ name: 'AbortError' })
    expect(signals.every(s => s.aborted)).toBe(true)
    await vi.advanceTimersByTimeAsync(10_000)
    await settle()
    expect(chunk).toHaveBeenCalledTimes(3)
    expect(chunkComplete).not.toHaveBeenCalled()
  })

  it('passes_the_caller_signal_to_single_upload', async () => {
    const caller = new AbortController()
    const small = fileOfSize(10)

    await uploadToFilePath('Order.Attachment', small, { signal: caller.signal })

    expect(upload).toHaveBeenCalledWith('Order.Attachment', small, undefined, caller.signal)
  })
})
