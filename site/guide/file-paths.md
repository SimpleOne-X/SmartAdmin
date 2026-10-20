# File Paths

An administrator configures once, in the back office, which file server and which folder attachments land in, how they are named, and who may upload and download them. Business code only knows a code: upload returns a relative path to store in your own table, and download takes the code and that path back.

The kernel's existing "Files" feature (the `FileUpload` component and the `/api/v1/sys/file/*` endpoints) is a separate system. The "Files" menu group holds two pages side by side: the original "Files" page covers that system, and the "File Paths" page covers this one. The endpoints and components are not interchangeable, so pick one for each business field and don't mix them.

## Allow the root directories first

A file server's root path must fall inside the directories listed in `SmartAdmin:FilePath:AllowedRoots`. With an empty whitelist every root path is rejected (`44152`), and the settings page shows a hint at the top asking you to configure it first.

The whitelist lives in the config file rather than the UI because it is a security boundary. If an administrator could type any root path in the UI, they could point files at the program directory or a static-site directory, or loop back to the local machine through a share such as `\\127.0.0.1\c$\…`. Adding a share means editing the config and restarting, which is done by an operator who can log in to the server.

```json
{
  "SmartAdmin": {
    "FilePath": {
      "AllowedRoots": [ "\\\\nas01\\files" ]
    }
  }
}
```

Matching is a prefix check on "whitelist directory + separator", so with `\\nas01\files` in the list, `\\nas01\files2` is not allowed. The check runs on save and again on every use (uploads and downloads return `44152`), so shrinking the whitelist makes any file server outside it unusable immediately.

The `SmartAdmin:FilePath` section has four items:

| Setting | Default | Purpose |
|---|---|---|
| `AllowedRoots` | empty array | Root directory whitelist, absolute paths. Empty means the feature is unusable |
| `StagingPath` | `./data/file-path-staging` | Staging directory for chunked uploads. A relative path resolves against ContentRoot |
| `StagingTtlHours` | `24` | A chunk session with no new chunk for this long is treated as abandoned and its staging is cleared. Must be greater than 0 |
| `LinkTtlMinutes` | `60` | Default validity of a signed link in minutes; the caller can pass another when signing. Must be greater than 0 |

A deployment that doesn't need the feature can add `"FilePath"` to `SmartAdmin:Api:DisabledModules`, and the routes of all four controllers disappear. The service registrations stay, so business code can still inject them.

## Register file servers

Open the "File Paths" page in the "Files" menu group and switch to the "File servers" tab. Register one row per share on a NAS. This page belongs to the system menus, so only the super admin and the built-in roles that were granted it can see it. Add, edit and delete require a recent identity check (`[RequireReauth]`, see [Auth & Security](/backend/auth-security)). Test connection writes nothing to the database and doesn't require it.

| Field | Notes |
|---|---|
| Name | Unique, up to 64 characters |
| Root path | Absolute path, must fall inside the whitelist. On Windows write `\\nas01\files\sub` or `//nas01/files/sub`; on Linux and Docker write the mount path as the container sees it, such as `/mnt/nas01/files` |
| Account | `DOMAIN\account` or `account`. Blank means access with the backend's own run identity. Greyed out on Linux |
| Password | Encrypted in the database, never returned by the API; the UI only shows "set" |
| Enabled | When disabled, every file path under it stops accepting uploads and downloads |

A few rules apply on save:

- With an account filled in, the root path must be a share path in the `\\host\share` form (`44153`). This is enforced only on Windows; Linux ignores the account anyway.
- **One host uses one account and password.** Windows accepts a single set of credentials per host, so rows whose root paths share a host name always carry the same account and password. When you add a row, or move a root path to another host, the account must match the rows already on that host, otherwise the save is rejected (`44154`). The account comparison is case-insensitive and blank counts as one value. Host names are compared case-insensitively and as written, with no name resolution, so `\\nas01` and `\\10.0.0.5` count as two hosts. Disabled rows take part in the comparison.
- A blank password means "unchanged". When an edit changes the host or account, the password must be entered again, and a blank one is rejected (`44157`): the old password is not carried over to the new host or paired with the new account. Clearing the account clears the password too.
- Saving a password needs a persistent data protection key, otherwise it is rejected (`44156`). See [Deployment](#deployment) below.
- A file server still referenced by file paths cannot be deleted (`44155`).

When you change the account or password of a row and its host stays the same, the other rows on that host change to the same account and password in the same transaction, and an account that differs only in letter case is rewritten in the new spelling too. Clearing the account switches every row on the host to the identity the backend runs as. The edit dialog says so under the password box. Changing only the name, the remark or other fields leaves the other rows alone.

"Test connection" in the edit dialog tests what is currently typed, with no need to save first, using the same rules as saving, except that it skips the same-host account check so you can try a new account before saving. Testing a new account drops the connections the backend holds to that host, and the other rows reconnect with their saved account the next time they are used. It tries, in order: connect (when an account is set), the root path exists, and it is readable (it stops at the first entry and never lists the whole directory). No result within 5 seconds reports "Timed out". When editing an existing row with the password left blank, the saved password is reused, provided neither host nor account changed.

The "online status" in the list checks in parallel whether each row's root path exists, with a 3-second timeout per row and no file writes. In a multi-replica deployment it reflects whether the replica that handled the request can reach the path.

After you change an account or password, no restart is needed: the next access uses the new credentials. The backend first drops every share it holds on that host with the old credentials, then connects with the new ones. In a multi-replica deployment, each replica does this on its own next access.

## Configure a file path

On the "File paths" tab, one row is one kind of business file.

| Field | Notes |
|---|---|
| Code | The key business code refers to. Letters, digits, `.`, `_` and `-` only, up to 64 characters, immutable once saved |
| Name | Up to 64 characters |
| File server | Pick from the registered servers |
| Subfolder | Relative to the server's root path, separated by `/`, such as `Order/Attachment`. Blank means the root itself. A missing folder is created on the first upload. These forms are rejected on save with `44112`: an absolute path, a `:`, an empty segment (such as `a//b`), a segment that starts with a dot (`..` included), and a segment that ends with a dot or a space. Downloads treat a dot-leading segment as nonexistent, so files stored there could never be fetched, which is why it is refused outright |
| Naming rule | See the table below |
| On name clash | Reject or overwrite; only meaningful for "business-specified path" |
| Read-only | Download only, no uploads, and no extensions or size limit to set |
| Extensions | Allowed upload extensions, lowercase with the dot, comma-separated, such as `.pdf,.png`. Required for writable rows |
| Max size | Per-file limit in MB. Required and greater than 0 for writable rows |
| Upload roles, download roles | Blank means every signed-in user |
| Enabled | When disabled, uploads, downloads and signed links are all rejected |

Extensions and the size limit don't inherit the upload policy of "Files", so the two uploads don't affect each other. The edit dialog shows the full folder and an example file name as you type.

Three naming rules:

| Naming rule | Relative path | Good for |
|---|---|---|
| Year/month/name_shortId | `{yyyy}/{MM}/{sanitized name}_{8 hex digits}{ext}`, such as `2030/01/quote_3f9c2e1a.pdf` | Attachments. Readable and never clashes |
| Business-specified path | `{sanitized subPath}/{sanitized file name}`, `subPath` may be empty, and each of its segments is sanitized by the file name rules too (`:` becomes `_`, for example) | Print templates, installers, anything whose path carries business meaning |
| Date/GUID | `{yyyyMMdd}/{GUID}{ext}`, such as `20300101/0198a3c4e5b27c61a8f90d1e2b3c4d5e.pdf` | Readability doesn't matter, only safety |

Year, month and date use the server's local time. The folders are meant to be browsed by people in a file explorer, so splitting by local date matches what they expect.

Validation and naming at upload time:

- **File name sanitizing**: `\ / : * ? " < > |` and control characters become `_`, a leading dot becomes `_`, trailing dots and spaces are removed, and Windows reserved names such as `CON`, `NUL` and `COM1` get a `_` prefix. The part before the extension keeps at most 60 characters, so the whole name fits the 255-byte limit common on NAS devices under UTF-8. The segments of a business-specified path's `subPath` are sanitized one by one by the same rules, and only an absolute path, an empty segment, a segment such as `.` or `..` that is empty once trailing dots and spaces are stripped, or a segment over 255 bytes after sanitizing is rejected (`44112`).
- The **extension** is taken from the sanitized name, lowercased, and must be in the whitelist. `quote.PDF` passes and is stored as `.pdf`; a file with no extension, such as `README`, is always rejected.
- A file over the **size** limit is rejected. Chunked uploads are checked once at init against the declared size, and again after merging against the actual size.
- A **name clash** can only happen with "business-specified path". With reject, the error is `44111`. With overwrite, the file is written to a temporary file in the same folder and then replaced. If the write fails halfway, the old file stays intact, and a file that is being downloaded can still be overwritten.

`subPath` comes from the frontend, so anyone who can upload to this row can overwrite any same-named file in the folder. Rows that overwrite should therefore have upload roles, and the settings page warns when "overwrite" is combined with empty upload roles.

::: warning Changing the file server or subfolder does not move old files
The relative paths in your business tables then point into the new folder, where the old files can't be found. Move the old files over before you change it, and the business data needs no change. The settings page shows the same hint when you edit.
:::

"Test connection" also tests what is currently in the dialog. For a writable row it writes, reads back and deletes a `.smartadmin-probe-{GUID}` file in the folder, creating the folder first if it is missing. A read-only row only checks that the folder exists.

## Who can upload and download

The upload and download endpoints have no menu buttons. They only require a signed-in user, then check the roles configured on the row:

- The super admin is always allowed.
- Blank roles: every signed-in user is allowed.
- Otherwise the user's **enabled** roles must intersect the list; disabled roles don't count.

Business users therefore don't need any system menu granted. The kernel only lets system menus be granted to built-in roles, so if upload and download depended on buttons of a system menu, business roles could never get the permission.

Per-user menu grants (allowing or denying one extra menu for a single user) have no effect here; only roles count. The file server's account belongs to the backend and has nothing to do with the signed-in user.

When the code doesn't exist or is disabled, `44101` is returned before any role check.

## Wire it into business code

### Upload: the `FilePathUpload` component

Import the component from `smart-admin-web`. It wraps `n-upload` and emits `uploaded` on success:

```vue
<FilePathUpload
  code="Order.Attachment"
  :show-file-list="false"
  @uploaded="out => (form.attachmentPath = out.path)"
>
  <n-button>Upload attachment</n-button>
</FilePathUpload>
```

| Prop or event | Notes |
|---|---|
| `code` | Required, the code registered on the settings page |
| `subPath` | Only used by the "business-specified path" naming rule: the file lands under this subpath of the folder. The server ignores it for the other rules |
| `uploaded` | Fires once a file uploads successfully, once per file when several are selected. The payload is `{ path, name, size, viewUrl }` |
| `loadingChange` | Fires at the start and end of each file's upload, for putting an "uploading" state on the trigger |

Other attributes such as `accept`, `multiple` and `show-file-list` pass straight through to `n-upload`, and the default slot is the trigger. On failure the component shows the error itself and marks the item red.

Files over 20 MB are chunked automatically: the component computes the SHA-256 of the whole file, initializes with the server to get the chunk size and count, uploads chunks concurrently, and merges at the end. Progress goes to the `n-upload` progress bar and stays at 99% or below during the chunk phase, reaching 100% only after the server finishes merging. This chunking has no instant upload and no resume, so a failure midway means re-uploading the whole file.

### Call the endpoints directly

A client that doesn't use the component calls the four endpoints below. All of them only require a signed-in user, and the roles come from the file path's configuration:

| Method | Route | Notes |
|---|---|---|
| POST | `/api/v1/sys/file-path/upload/{code}` | Single-file upload, multipart: `file`, optional `subPath` |
| POST | `/api/v1/sys/file-path/upload/{code}/chunk/init` | Chunk init: `fileName`, `size`, `sha256` (64 hex digits), optional `subPath`. Returns `uploadId`, `chunkSize` and `chunkCount` only after read-only, extension, size and role checks pass |
| POST | `/api/v1/sys/file-path/upload/{code}/chunk` | Upload one chunk, multipart: `uploadId`, `index` (from 0), `chunk` |
| POST | `/api/v1/sys/file-path/upload/{code}/chunk/complete` | Body `uploadId`. Merges, verifies SHA-256 and size, and writes by the naming rule |

Both kinds of upload return `{ path, name, size, viewUrl }`. `path` is the relative path, `name` is the sanitized original file name, and `viewUrl` is a signed link (see [Signed links](#signed-links) below).

The server decides the chunk size; the client cuts by `chunkSize`, and the last chunk may be shorter. The `uploadId` is generated by the server and bound to the current user and code, so nobody else can use it. Chunks are staged in `StagingPath/{uploadId}/` and deleted after the merge; a session with no new chunk for longer than `StagingTtlHours` is swept by a background job.

The single-file endpoint keeps the framework's default request body limit, 30,000,000 bytes (about 28.6 MB) on both Kestrel and IIS, which the kernel doesn't raise. The component sends files over 20 MB through chunks, so a larger size limit on the row is not held back by it; if you write your own client, send large files in chunks too.

### What the business table stores

Store `path`, plus `name` when you need to show the original file name. Don't store the full UNC path, and don't store `viewUrl`:

- A relative path doesn't include the server's root path, so after you change the file server's root path, you only move the old files and the business data stays as it is.
- The file name inside `path` may have been changed by the naming rule, so to show the original file name you have to store `name` separately.
- `viewUrl` expires, so the backend signs a fresh one when needed.

### Download

The frontend fetches the bytes with `filePathFileApi.download` and saves them with `triggerBlobDownload`:

```ts
import { filePathFileApi, triggerBlobDownload } from 'smart-admin-web'

const blob = await filePathFileApi.download('Order.Attachment', row.attachmentPath, row.attachmentName)
triggerBlobDownload(blob, row.attachmentName)
```

Underneath it is `GET /api/v1/sys/file-path/download/{code}?path=&name=`, which requires a signed-in user who meets the row's download roles. The file is always returned as an attachment with `nosniff`, and Range requests are supported.

- `name` is optional and only decides the file name the browser saves under. After sanitizing, its extension is forced to the real extension of the file on disk, so a caller can't use it to turn `.pdf` into `.exe`. Without it, the name on disk is used.
- Download ignores the extension whitelist, because a read-only row may hold old files of any extension.
- A path where any segment starts with `.` is treated as a missing file (`44113`). Probe files from test connection, temporary files from overwrites and hidden system files all look like that.

### Call the services from the backend

Delete has no HTTP endpoint, only a backend service method that your business code calls when it deletes a document or swaps an attachment. The services below are in the `SmartAdmin.Services` namespace, and the signer is in `SmartAdmin.Core`:

| Service | Method | Purpose |
|---|---|---|
| `IFilePathUploadService` | `UploadAsync`, `DeleteAsync` | Write a backend-generated file (an exported report, say) into the folder, or delete one file from it |
| `IFilePathDownloadService` | `OpenReadAsync(code, path)` | Returns an opened read-only stream; the caller disposes it |
| `IFilePathDownloadService` | `GetFullPathAsync(code, path)` | Returns the full path, for a program that has to open the file itself. Path arithmetic only, with no check that the file exists |
| `IFilePathLinkSigner` | `BuildUrl(code, path, ttl?)` | Signs a direct link |

```csharp
public class OrderAttachmentService(IFilePathLinkSigner signer, IFilePathUploadService uploads)
{
    // The business table holds only the path; the preview link is signed on demand
    public string PreviewUrl(string path) => signer.BuildUrl("Order.Attachment", path);

    // When a document is deleted or its attachment replaced, the business deletes the file itself
    public Task RemoveFileAsync(string path, CancellationToken cancellationToken) =>
        uploads.DeleteAsync("Order.Attachment", path, cancellationToken);
}
```

Backend code that calls these services directly gets **no role check**; the business code that calls them is responsible. The rule checks (read-only, extension, size, path) still apply.

All of these built-in services are registered with `TryAdd`; to replace one, follow [Replace built-in services](/guide/replace-service).

## Signed links

An `<img src>` can't carry an `Authorization` header, and the client may not even hold a token. Signed links are for exactly that: anyone can read through one, but it can't be forged, so only whoever has the link gets the file.

A link comes from two places: the `viewUrl` the upload endpoints return, and backend code calling `IFilePathLinkSigner.BuildUrl` on demand. The latter covers, for example, a list endpoint that attaches a preview link to each row's image, or a download link handed to a client that doesn't sign in. What comes out is a relative URL starting with `/api/v1/sys/file-path/download/{code}/view`.

- **Who gets one**: `viewUrl` is returned only when the uploader also meets the row's download roles; otherwise it is `null`. Being able to upload doesn't mean being able to download, and on a row that overwrites by name, what is read within the link's validity may already be content someone else overwrote it with. Calling `BuildUrl` directly from the backend makes no such check, so who receives the link is up to your business code.
- **Validity**: always set, and no permanent link can be issued. The default is `LinkTtlMinutes` (60 minutes), and a different value can be passed when signing.
- **Tamper-proof**: the signature covers the code, the relative path and the expiry time, so changing any of them fails verification. Missing parameters, a wrong signature or an expired link all return `403`, and give no way to probe whether a file exists.
- **How it is shown**: `.png`, `.jpg`, `.jpeg`, `.gif`, `.webp`, `.bmp` and `.pdf` display inline; everything else is downloaded as an attachment, `.svg` and HTML included. The decision is made on the extension, never on the type the client reported at upload.
- **Revoking**: there is no per-link revocation. Wait for it to expire, or rotate `Jwt:SecretKey`, which invalidates every signature and also every login token.

Store the relative path in the business table, not the link.

## Deployment

### Windows: two ways to authenticate

**Fill in an account and password.** On the file server, enter the account and password, and on Windows the kernel connects to the share with that account. Write the root path as a share path such as `\\nas01\files`; the password is stored encrypted. One host can only use one account, see [Register file servers](#register-file-servers) above.

**Leave the account blank and use the backend's run identity.** Run the backend under a dedicated account (the IIS application pool identity, or the logon identity of a Windows service) and leave both the account and password of the file server blank. When the server and the NAS are in the same domain, use a domain account and write the host name rather than an IP in the root path, so Kerberos can be used. Nothing is stored in the database this way.

On the file server side: keep the access account out of the administrators group and give it only the share permissions it needs, set the minimum SMB protocol to SMB2, and turn SMB1 off.

### Linux and Docker: host mounts

On Linux the kernel doesn't connect to the file server. The host mounts the share over CIFS, and the account goes in the mount credentials:

```ini
# /etc/smb-nas01.cred, mode 600
username=svc-smartadmin
password=...
```

```text
# /etc/fstab
//nas01/files  /mnt/nas01/files  cifs  credentials=/etc/smb-nas01.cred,vers=3.1.1,uid=1654,gid=1654,_netdev  0  0
```

`vers=3.1.1` is the SMB protocol version; write `vers=3.0` if the NAS doesn't support it. `uid` and `gid` must match the user the backend runs as inside the container: the `app` user in the official .NET images is 1654, and if you change images, confirm it with `docker exec <container> id` first.

Then map the mount point into the container and put the mount root in the whitelist:

```yaml
services:
  app:
    volumes:
      - /mnt/nas01/files:/mnt/nas01/files
    environment:
      SmartAdmin__FilePath__AllowedRoots__0: /mnt/nas01
```

On the settings page, enter the root path as the container sees it, `/mnt/nas01/files`, and leave the account and password blank. On Linux the page greys out those two inputs, with a hint that the host supplies the account and password when it mounts. A row that already has an account (data moved over from a Windows environment, say) has the account ignored on Linux, with one warning log the first time it is used.

### Data protection key

When you save a password, the kernel first checks whether the data protection key is an in-process ephemeral key, and rejects the save if it is (`44156`): an ephemeral key is lost on restart, so the stored password could never be decrypted. In development the kernel generates a development key and writes it to disk, so nothing is needed. Production must set `SmartAdmin:Security:DataProtection:Key` explicitly. A multi-replica deployment must also share the same value; for how to set it, see [Containers & Multi-Replica](/guide/deployment/docker). The same key is used for TOTP, external login and AI provider keys.

If you only use the "blank account" way of authenticating, the database holds no password and you don't need it.

### Multiple replicas

In a multi-replica deployment the chunks of one upload may land on different replicas, so put `StagingPath` on a shared volume, or enable session affinity on the load balancer. On Linux every replica must mount the same share, at the same mount path.

## Troubleshooting

### The folder cannot be reached

When reaching the file server fails, the UI shows a reason. Test connection and the online status return it as a reason value in their result, while uploads and downloads return `44114` with the reason in its arguments:

| Reason | What to check |
|---|---|
| Access denied | The account has no permission on this share or folder |
| Network path not found | The host name doesn't resolve, or the host is unreachable |
| Share does not exist | The host is reachable but has no share by that name |
| Wrong account or password | Includes an expired password |
| Account is locked or disabled | Unlock it on the domain controller or the NAS |
| Credential conflict | Windows error 1219, see below |
| Timed out | No result within 5 seconds for test connection, or 3 seconds for the online status |
| Folder does not exist | A read-only file path whose folder hasn't been created |
| File server busy | More than 16 file operations are running on this server at once; try again shortly |
| Unknown error | None of the above; look in the backend log for the full exception and the system error code |

When the whitelist was shrunk later and the root path now falls outside it, uploads and downloads return `44152` directly and never reach the reasons above. Test connection and the online status don't touch that path, and their reason shows as "Access denied".

On a wrong account or password the kernel doesn't retry on every request, so a domain account isn't locked within minutes. It tries again only after you correct the configuration or click "Test connection". Network failures are retried after 30 seconds.

After a network failure such as network path not found or a timeout, uploads, downloads and deletes on that server return `44114` straight away for 30 seconds, with the reason of that failure, and don't touch the file server. That way requests don't each hang until the system's network timeout and tie up the backend's threads, and sign-in and the other endpoints keep responding. After 30 seconds one request is let through as a trial: success restores access, failure holds requests back for another 30 seconds. Test connection and the online status are not held back, and a success restores access at once. Each server runs at most 16 file operations at a time, and requests beyond that get "File server busy" right away. This state lives in each replica's own memory.

### Credential conflict

One Windows logon session accepts a single set of credentials per host, and a second set gets error 1219 (`ERROR_SESSION_CREDENTIAL_CONFLICT`), which the UI shows as "Credential conflict". The backend log, which is written in Chinese, records it as `Win32 错误 1219`, so search for the number 1219. There are two common sources:

- Different accounts configured on the same host. Saving already keeps the rows of one host in step, unless the data was changed directly in the database.
- Another program in the backend process's logon session is already connected to the same host with other credentials. When you develop locally with `dotnet run`, the backend runs in your own logon session, and if Explorer has the same NAS open with your account, that is exactly this case. In development, leave the file server's account blank and the backend accesses it with your own identity.

On 1219 the backend drops every share it holds on that host and reconnects once, and reports the error only if the conflict remains. When deploying, run the backend under its own identity and don't share it with other programs.

## Error codes

File paths take `44100`–`44199`, following the segments in [Error codes](/backend/error-codes).

| Code | Meaning |
|---|---|
| `44101` | The code doesn't exist or is disabled |
| `44102` | The code already exists |
| `44103` | The code format is invalid |
| `44104` | A writable row lacks the extension whitelist or the size limit |
| `44105` | The extension whitelist format is invalid: each must start with a dot, have at most 15 letters or digits, and be comma-separated |
| `44106` | A read-only row doesn't allow upload or delete |
| `44107` | No upload permission |
| `44108` | No download permission |
| `44109` | The extension isn't in the whitelist, files with no extension included |
| `44110` | Over the size limit |
| `44111` | A file with the same name already exists |
| `44112` | Invalid path. For the `path` of a download or delete and for a configured subfolder: an absolute path, a drive letter, `:`, control characters, an empty segment, `.` or `..`, a segment ending in a dot or space, or an escape from the folder; in a subfolder, a segment starting with a dot counts too. The `subPath` of a business-specified path is sanitized segment by segment first, and only an absolute path, an empty segment, `.`, `..`, or a segment over 255 bytes after sanitizing returns this code |
| `44113` | The file doesn't exist, including any path with a segment that starts with a dot |
| `44114` | The folder cannot be reached, with a reason attached |
| `44115` | A chunk is missing; upload it and merge again |
| `44116` | The SHA-256 after merging doesn't match the declared one |
| `44117` | Invalid upload session: malformed or unknown id, expired, not owned by the current user or code, or already claimed by another merge. At init, a `sha256` that isn't 64 hex digits or a negative `size`, a chunk index out of range, and a chunk whose length doesn't match the expected length also return this code |
| `44150` | The file server doesn't exist or is disabled |
| `44151` | The file server name already exists |
| `44152` | The root path isn't in the whitelist, checked on save and on every upload and download |
| `44153` | An account is set but the root path isn't in the `\\host\share` form |
| `44154` | The host already has another account configured |
| `44155` | The file server is still referenced by file paths and cannot be deleted |
| `44156` | The data protection key is an in-process ephemeral key, so saving a password is refused |
| `44157` | An edit changed the host or account but left the password blank |

The way out of `44154`: to give a host a new account, edit the account and password of any one row on that host, and the other rows change with it. For a new row, or a row moving to that host, first enter the account the host already uses, save, and then change the whole host if needed.
