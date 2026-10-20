import { beforeEach, describe, expect, it, vi } from 'vitest'

// 上传端点由 filePathFileApi 提供,这里只验编排:走单次还是分片、分片怎么切、进度怎么报。
const api = vi.hoisted(() => ({
  filePathFileApi: {
    upload: vi.fn(),
    chunkInit: vi.fn(),
    chunk: vi.fn(),
    chunkComplete: vi.fn(),
  },
}))
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
    expect(upload).toHaveBeenCalledWith('Order.Attachment', file, undefined)
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
    expect(chunkInit).toHaveBeenCalledWith('Order.Attachment', {
      fileName: 'a.bin',
      size: file.size,
      sha256: expect.stringMatching(/^[0-9a-f]{64}$/),
      subPath: undefined,
    })
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
    expect(chunkComplete).toHaveBeenCalledWith('Order.Attachment', 'u1')

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
    expect(upload).toHaveBeenCalledWith('Order.Attachment', small, 'a/b')

    const big = fileOfSize(FILE_PATH_CHUNK_THRESHOLD + 1)
    initFor(big.size, 5)
    await uploadToFilePath('Order.Attachment', big, { subPath: 'a/b' })
    expect(chunkInit).toHaveBeenCalledWith(
      'Order.Attachment',
      expect.objectContaining({ subPath: 'a/b' }),
    )
  })
})
