# FilePathUpload

按「文件路径」编码上传的触发器。封 `n-upload` 的 `:custom-request`,内部走 `uploadToFilePath`(自动带 Bearer),成功 `emit('uploaded', out)`。
上传目的地是管理员在「文件路径」配置页里登记的文件夹(可以是文件服务器上的共享),和 `FileUpload` 写入的内核文件表是两条互不相干的路。

`inheritAttrs:false` + `v-bind="$attrs"`:`accept`、`multiple`、`show-file-list`、`:max` 等一切透传给 `n-upload`,不重复声明。默认 slot 即触发器(通常放一颗按钮)。

## Props

| 属性      | 类型     | 默认 | 说明                                                                                               |
| --------- | -------- | ---- | -------------------------------------------------------------------------------------------------- |
| `code`    | `string` | —    | 文件路径编码,必填。                                                                                |
| `subPath` | `string` | —    | 仅该文件路径的命名规则是「业务指定路径」时有用:文件落在文件夹下的这个子路径里;其余规则服务端忽略。 |

## Emits

| 事件            | 载荷                   | 说明                                                                                            |
| --------------- | ---------------------- | ----------------------------------------------------------------------------------------------- |
| `uploaded`      | `FilePathUploadOutput` | 单个文件上传成功后触发(多选时每个文件各触发一次)。载荷是 `{ path, name, size, viewUrl }`。      |
| `loadingChange` | `[loading: boolean]`   | 单个文件上传的起止各触发一次(`try/finally` 兜底,失败也会收到 `false`)。给触发器加"上传中"态用。 |

## 业务表存什么

- **存 `path`**:相对文件夹的路径,`/` 分隔。以后下载、删除都凭它加上编码定位文件,所以它是业务表里必须落库的那一列。
- **需要显示原始文件名时再存 `name`**:`name` 是清洗后的原始文件名;`path` 里的文件名可能被命名规则改过(例如按日期加序号),不一定还是原名。
- `size` 按需存;不要存 `viewUrl`(见下)。

## 大文件自动分片

文件大小超过 20 MB(`FILE_PATH_CHUNK_THRESHOLD`,等于 20 MB 时仍是单次上传)就自动分片:在浏览器内存里算整文件 SHA-256 → 向服务端 `chunk/init` 拿分片大小和片数 → 并发 3 路传片 → `chunk/complete` 合并。
调用方不用选,组件和 `uploadToFilePath` 都是同一套入口;进度回给 `n-upload` 进度条,传片阶段最多到 99%,服务端合并完成后才到 100%。
同一次上传里,分片和合并遇到网络错误、超时、5xx、文件夹暂时无法访问(`44114`)时分别在 1、2、4 秒后重试;合并报缺片(`44115`)就补传那一片再合并一次。
任一片最终失败,其余在途的分片随即取消;组件卸载时在途的上传也取消,且不再弹错误。
关掉页面或重新选文件要整个重传,没有秒传。`uploadToFilePath` 自己调时可以传 `signal` 中止。

## viewUrl 可能为 null

`viewUrl` 是给 `<img src>` 这类带不了 `Authorization` 头的场景用的匿名签名直链,会过期。
只有当前用户对这个文件路径有**下载**权限时服务端才会签发,所以只有上传权限的用户拿到的是 `null`,调用方要处理。
需要长期展示就存 `path`,展示时再调 `filePathFileApi.download(code, path)` 取字节。

## 用法

```vue
<!-- 订单附件:纯触发器,不显示 n-upload 自带文件列表,上传成功把路径写进表单 -->
<FilePathUpload
  code="Order.Attachment"
  :show-file-list="false"
  @uploaded="out => (form.attachmentPath = out.path)"
>
  <n-button>{{ t('order.attachment') }}</n-button>
</FilePathUpload>
```

失败在组件内 `message.error(translateError(e))` 并 `onError()`(n-upload 该项标红);成功 `onFinish()`。
「文件夹无法访问:原因」这类带原因码的错误,原因由 `translateError` 统一翻成当前语言。
