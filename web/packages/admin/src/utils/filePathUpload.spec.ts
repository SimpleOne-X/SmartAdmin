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
    const file = fileOfSize(FILE_PATH_CHUNK_THRESHOLD + 1)
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
