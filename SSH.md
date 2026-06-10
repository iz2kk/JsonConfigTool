# SSH Manager Roadmap cho ConfigTool

> Mục tiêu: xây dựng Tab SSH trong ConfigTool theo hướng gộp **PuTTY + WinSCP + PuTTYgen + SSH Server mini** vào cùng một trang `/ssh`, bên trong chia thành nhiều tab nhỏ.  
> App là **MAUI Blazor Hybrid Windows**, ưu tiên UI single-page, SignalR local server, xử lý lỗi mềm, không làm crash app.

---

## 0. Mục tiêu tổng thể

Tab SSH cuối cùng cần có các nhóm chức năng:

```txt
/ssh - SSH Manager
├─ Tổng quan
├─ SSH Server
├─ Session / Profiles
├─ Terminal
├─ SFTP Files
├─ Transfer Queue
├─ Keys
├─ Tunnels
├─ Sync / Deploy
├─ Scripts
├─ Logs
├─ Settings
└─ Import / Export
```

Ý tưởng chính:

```txt
1 profile server chung
 -> mở Terminal như PuTTY
 -> mở Files như WinSCP
 -> quản lý Key như PuTTYgen
 -> deploy/sync dự án qua SFTP
 -> tạo SSH Server mini local để máy khác/app khác có thể kết nối vào thư mục được chọn
```

---

## 1. Nguyên tắc bắt buộc khi làm module SSH

- Không nhét toàn bộ logic vào `SshAdmin.razor`.
- UI chỉ gọi service/hub, logic nặng nằm trong `Services/`.
- Mọi connect/transfer/command/tunnel phải có `CancellationToken` nội bộ và nút cancel.
- Không lưu password/private key passphrase dạng plain text.
- Lỗi SSH/SFTP phải báo mềm trên UI/log, không làm crash app.
- Terminal, SFTP, Tunnel, Deploy đều dùng chung `SshProfileDto` và `SshConnectionFactory`.
- Nếu dùng SignalR local server thì service mới phải đăng ký cả ở:
  - `MauiProgram.cs`
  - `Services/ConfigSignalRHost.cs`
- Folder dialog ưu tiên dùng native Windows folder picker hiện có hoặc mở rộng từ `IConfigFolderPicker`.
- Với SSH Server local: phải cho chọn IP bind, port, user, password, thư mục root, trạng thái chạy/dừng và log truy cập.

---

## 2. Layout tổng thể của trang SSH

```txt
┌─────────────────────────────────────────────────────────────┐
│ SSH Manager                                                  │
│ Profile: [VPS Main ▼] [Connect] [Disconnect] [Test]          │
│ Local SSH Server: [Stopped/Running] [Start] [Stop]           │
├─────────────────────────────────────────────────────────────┤
│ [Overview] [SSH Server] [Profiles] [Terminal] [Files]        │
│ [Queue] [Keys] [Tunnels] [Sync] [Scripts] [Logs] [Settings]  │
├─────────────────────────────────────────────────────────────┤
│ Nội dung tab đang chọn                                       │
└─────────────────────────────────────────────────────────────┘
```

Tab nhỏ cần dùng style giống `GitAdmin.razor` / `SqlAdmin.razor`: nav-pills, card, modal rộng, status message, loading/cancel.

- [x] Trước khi vào mỗi tab có màn hình loading riêng; load dữ liệu xong thì tự mất.

---

# Phase SSH-00 — Dựng khung trang SSH

## Mục tiêu

Thêm trang `/ssh` vào ConfigTool, có layout, nav chính, tab nhỏ và status bar.

## File thêm/sửa dự kiến

```txt
Components/Pages/SshAdmin.razor
Models/SshDtos.cs
Services/SshConfigService.cs
Services/SshLogService.cs
wwwroot/vendor/configtool-ssh.js
wwwroot/app.css
MauiProgram.cs
Components/Layout/MainLayout.razor
languages/vi-vn.json
languages/en-us.json
```

## UI cần có

```txt
SSH Manager
[Reload] [Cancel current] [Cancel all] [Open config folder]

Tabs:
[Tổng quan] [SSH Server] [Profiles] [Terminal] [SFTP Files] [Queue]
[Keys] [Tunnels] [Sync] [Scripts] [Logs] [Settings]
```

## Checklist

- [x] Có menu SSH trên topbar.
- [x] Vào `/ssh` không lỗi.
- [x] Có tab nhỏ chuyển qua lại không reload trang.
- [x] Có `StatusMessage`, `IsBusy`, `CancelRequested`.
- [x] Có vùng log nhanh cuối trang.

---

# Phase SSH-SERVER-01 — Tạo SSH Server local trong ConfigTool

## Mục tiêu

Thêm tab **SSH Server** để ConfigTool có thể chạy một SSH/SFTP server mini cục bộ. Server này cho phép máy khác hoặc tool khác kết nối vào một thư mục được chọn, phục vụ debug, trao đổi file, test SFTP/SSH nội bộ.

## Yêu cầu chính từ user

- Có thể sửa **IP bind**.
- Có thể sửa **port**.
- Có thể sửa **user**.
- Có thể sửa **pass**.
- Có **folder dialog** để chọn **thư mục SSH Server/root folder**.
- Có Start/Stop/Restart server.
- Có log kết nối/truy cập.

## UI đề xuất

```txt
SSH Server
┌─────────────────────────────────────────────────────────────┐
│ Status: Stopped / Running                                    │
│ Bind IP:      [0.0.0.0              ▼]                       │
│ Port:         [2222                  ]                       │
│ Username:     [configtool            ]                       │
│ Password:     [••••••••              ] [Show] [Generate]     │
│ Root Folder:  [D:\ConfigToolSshRoot  ] [Browse...] [Open]     │
│ Allow SFTP:   [x]                                             │
│ Allow Shell:  [ ]                                             │
│ Read Only:    [ ]                                             │
│ Auto Start:   [ ]                                             │
│                                                             │
│ [Save] [Test Port] [Start] [Stop] [Restart]                  │
└─────────────────────────────────────────────────────────────┘
```

## Model dự kiến

```csharp
public sealed class LocalSshServerConfigDto
{
    public string BindIp { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 2222;
    public string Username { get; set; } = "configtool";
    public string? PasswordProtected { get; set; }
    public string RootFolder { get; set; } = "";
    public bool AllowSftp { get; set; } = true;
    public bool AllowShell { get; set; } = false;
    public bool ReadOnly { get; set; } = false;
    public bool AutoStart { get; set; } = false;
    public bool AllowPasswordAuth { get; set; } = true;
    public bool AllowKeyAuth { get; set; } = false;
    public DateTimeOffset UpdatedAt { get; set; }
}
```

## File config

Lưu vào:

```txt
<AppStartup>/config/ssh-server.json
```

Không lưu password thô. Password phải đi qua `SshSecretService` hoặc vault tương tự.

## Service dự kiến

```txt
Services/LocalSshServerConfigService.cs
Services/LocalSshServerHostService.cs
Services/LocalSshServerRuntimeService.cs
Services/LocalSshServerLogService.cs
Models/LocalSshServerDtos.cs
```

## Logic cần có

```txt
Save config
 -> validate IP/port/user/pass/root folder
 -> nếu port đang chạy thì cảnh báo phải Restart
 -> lưu config

Browse root folder
 -> mở Windows folder dialog
 -> ghi RootFolder vào form
 -> có nút Open Explorer

Start server
 -> kiểm tra RootFolder tồn tại
 -> kiểm tra port chưa bị chiếm
 -> bind IP + port
 -> bật SFTP nếu AllowSftp = true
 -> bật shell nếu AllowShell = true, mặc định tắt để an toàn
 -> ghi log started

Stop server
 -> ngắt session đang kết nối
 -> giải phóng port
 -> ghi log stopped
```

## Luật an toàn

- [x] Default bind nên là `127.0.0.1`, không phải `0.0.0.0`.
- [x] Nếu user chọn `0.0.0.0`, UI phải hiện cảnh báo đang mở cho mạng LAN.
- [x] Mặc định `AllowShell = false`, chỉ bật SFTP trước.
- [x] Không cho root folder rỗng.
- [x] Không cho root folder là ổ hệ thống.
- [x] Password không được rỗng nếu bật password auth.
- [x] Có nút generate password mạnh.
- [ ] Log không được ghi password.

## Checklist

- [x] Tab SSH Server hiển thị form đầy đủ.
- [x] Browse folder chọn được thư mục root.
- [x] Save/load config hoạt động.
- [x] Start/Stop/Restart có status runtime trong UI.
- [x] Test port báo port rảnh/bị chiếm.
- [ ] SFTP client khác có thể connect vào server local.
- [ ] Log có IP client, user, action, path, thời gian.

---

# Phase SSH-SERVER-02 — SSH Server Users / Permission / Folder Mapping

## Mục tiêu

Mở rộng SSH Server local để quản lý nhiều user và giới hạn quyền theo thư mục.

## UI đề xuất

```txt
Users
User | Root Folder | Read | Write | Delete | Shell | Enabled | Actions
```

## Chức năng

- [ ] Thêm/sửa/xóa user SSH Server.
- [ ] Mỗi user có root folder riêng.
- [ ] Chọn folder bằng folder dialog.
- [ ] Phân quyền: read, write, delete, shell.
- [ ] Disable user không cần xóa.
- [ ] Reset password.
- [ ] Copy connection string.

## Model dự kiến

```csharp
public sealed class LocalSshServerUserDto
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    public string? PasswordProtected { get; set; }
    public string RootFolder { get; set; } = "";
    public bool CanRead { get; set; } = true;
    public bool CanWrite { get; set; } = true;
    public bool CanDelete { get; set; } = false;
    public bool CanShell { get; set; } = false;
    public bool Enabled { get; set; } = true;
}
```

## Checklist

- [x] Có CRUD user server.
- [x] Folder dialog hoạt động cho từng user.
- [ ] Permission được enforce thật khi upload/delete/download.
- [ ] User bị disable không đăng nhập được — cần SSH/SFTP protocol core thật ở phase sau.

## Ghi chú triển khai 2026-06-10

- Đã dựng `/ssh`, nav topbar, tab nhỏ, status, log nhanh.
- Đã thêm `config/ssh-server.json`, mã hóa password qua `SshSecretService`, CRUD user/quyền/folder mapping.
- Đã có Test Port và Start/Stop/Restart dạng TCP listener an toàn để kiểm tra bind port.
- SSH/SFTP protocol core thật và enforce quyền upload/download/delete sẽ nối ở phase SFTP/SSH server runtime sau.

---

# Phase SSH-01 — Config JSON + Profile Manager

## Mục tiêu

Làm phần **Session Manager** giống PuTTY Session + WinSCP Site Manager.

## File config

```txt
<AppStartup>/config/sshconfig.json
```

## Model chính

```csharp
public sealed class SshProfileDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Protocol { get; set; } = "ssh-sftp";

    public string Username { get; set; } = "";
    public string AuthType { get; set; } = "password";
    public string? PasswordProtected { get; set; }
    public string? PrivateKeyId { get; set; }
    public string? PrivateKeyPath { get; set; }
    public string? PrivateKeyPassphraseProtected { get; set; }

    public string LocalStartPath { get; set; } = "";
    public string RemoteStartPath { get; set; } = "/";

    public string HostKeyFingerprint { get; set; } = "";
    public bool TrustHostKey { get; set; }

    public int TimeoutSeconds { get; set; } = 30;
    public int KeepAliveSeconds { get; set; } = 30;

    public string ProxyType { get; set; } = "none";
    public string? ProxyHost { get; set; }
    public int? ProxyPort { get; set; }

    public List<string> Tags { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

## Chức năng

- [ ] Thêm/sửa/xóa profile.
- [ ] Duplicate profile.
- [ ] Test connect.
- [ ] Đặt profile mặc định.
- [ ] Tag profile: VPS, Termux, Ubuntu, Caddy, MySQL.
- [ ] Tìm kiếm profile.
- [ ] Import/export profile nhưng không export secret.

---

# Phase SSH-02 — Secret Vault / Mã hóa mật khẩu và passphrase

## Mục tiêu

Không lưu password/key passphrase dạng plain text.

## File thêm

```txt
Services/SshSecretService.cs
Models/SshSecretDtos.cs
```

## Dữ liệu lưu

```txt
<AppStartup>/config/credential-vault.json
```

Ví dụ lưu protected value:

```json
{
  "secretId": "profile-main-root-password",
  "protectedValue": "base64-encrypted-data",
  "scope": "CurrentUser"
}
```

## Checklist

- [x] Profile không chứa password thô.
- [ ] SSH Server config không chứa password thô.
- [x] Có nút Save Password.
- [ ] Có nút Clear Password.
- [~] Password được mã hóa, nhưng OpenSSH `ssh.exe` không nhận password qua argument an toàn; test connect thực tế ưu tiên private key/agent.

---

# Phase SSH-03 — SSH Core Service

## Mục tiêu

Có service lõi để mở SSH connection dùng chung cho Terminal, SFTP, Tunnel, Script, Deploy.

## File thêm/sửa thực tế

```txt
Services/SshCoreService.cs
Services/SshProfileConfigService.cs
Models/SshDtos.cs
MauiProgram.cs
```

Ghi chú: phase này dùng OpenSSH `ssh.exe`/`scp.exe` có sẵn trên Windows để tránh kéo dependency nặng. Password được lưu mã hóa nhưng không truyền qua command-line; key/agent là đường ổn định.

## Service map

```txt
SshConnectionFactory
├─ BuildConnectionInfo(profile)
├─ BuildPasswordAuth()
├─ BuildPrivateKeyAuth()
├─ ValidateHostKey()
└─ CreateSshClient()

SshSessionRegistry
├─ Lưu session đang mở
├─ Hủy session theo tab
├─ Hủy tất cả
└─ Cleanup khi thoát app
```

## Checklist

- [~] Connect SSH bằng password: đã lưu secret, chưa truyền vào ssh.exe vì không an toàn.
- [x] Connect SSH bằng private key/agent qua OpenSSH options.
- [x] Run command đơn.
- [x] Run command có timeout.
- [x] Keep alive option.
- [x] Cancel command qua CancellationToken/nút Cancel current.
- [x] Log stdout/stderr.

---

# Phase SSH-04 — Terminal giống PuTTY cơ bản

## Mục tiêu

Làm tab **Terminal** mở console SSH tương tác.

## File thêm/sửa thực tế

```txt
Services/SshCoreService.cs
Components/Pages/SshAdmin.razor
wwwroot/app.css
```

Ghi chú: terminal hiện là command runner kiểu PuTTY basic, chưa phải PTY/xterm realtime shell. PTY/xterm thật sẽ nối phase sau.

## UI

```txt
Terminal
[Profile select] [Connect] [Disconnect] [Reconnect] [Clear] [Copy] [Log ON/OFF]

┌──────────────────────────────────────────────┐
│ root@server:~#                               │
└──────────────────────────────────────────────┘
```

## Checklist

- [x] Chọn profile + test SSH/command runner.
- [~] Gõ lệnh từng command; realtime PTY để phase xterm/ConPTY sau.
- [~] Nhận stdout/stderr sau khi command kết thúc; streaming realtime để phase sau.
- [x] Cancel current để hủy process đang chạy.
- [~] Terminal output responsive; resize PTY thật để phase xterm sau.
- [x] Copy output + paste vào input command.
- [x] Clear screen/output.
- [~] Có profile select/history; nhiều terminal tab thật để phase sau.

---

# Phase SSH-05 — Terminal nâng cao giống PuTTY

## Mục tiêu

Thêm cấu hình terminal nâng cao.

## Chức năng

- [x] Font size.
- [x] Theme sáng/tối.
- [x] Scrollback lines UI setting.
- [x] Cursor style setting placeholder.
- [x] Bell on/off setting placeholder.
- [x] Copy on select setting placeholder.
- [x] Paste warning setting placeholder.
- [x] Session/command log.
- [x] Auto reconnect setting placeholder.
- [~] Proxy fields nằm trong profile model; core xử lý proxy sẽ làm sau.

---

# Phase SSH-06 — SFTP File Explorer giống WinSCP cơ bản

## Mục tiêu

Làm tab **SFTP Files** dạng 2 panel.

## File thêm/sửa thực tế

```txt
Services/SftpExplorerService.cs
Services/SftpTransferQueueService.cs
Models/SshDtos.cs
Components/Pages/SshAdmin.razor
wwwroot/app.css
```

Ghi chú: Explorer 2 panel giống Windows/WinSCP dùng local filesystem + SSH command để list remote Linux/Termux/Ubuntu. Transfer dùng `scp.exe`.

## UI

```txt
Local Explorer                  Remote Explorer
D:\LapTrinh\ConfigTool          /var/www/configtool
├─ file                          ├─ file
├─ folder                        ├─ folder

[Upload →] [← Download] [Refresh] [New Folder] [Delete] [Rename]
```

## Checklist

- [x] Duyệt local folder.
- [x] Duyệt remote folder qua SSH command.
- [x] Path bar + Up local/remote.
- [x] Upload file/folder qua queue scp.
- [x] Download file/folder qua queue scp.
- [x] Rename/delete/new folder remote.
- [x] Chmod remote.
- [x] Copy local/remote path.
- [~] Chọn path remote/local; nút open terminal here sẽ nối với PTY phase sau.

---

# Phase SSH-07 — Transfer Queue giống WinSCP

## Mục tiêu

Upload/download nhiều file, folder, có queue, progress, retry.

## File thêm/sửa thực tế

```txt
Services/SftpTransferQueueService.cs
Models/SshDtos.cs
Components/Pages/SshAdmin.razor
```

## Checklist

- [x] Queue upload nhiều item.
- [x] Queue download nhiều item.
- [x] Upload folder dùng scp -r.
- [x] Download folder dùng scp -r khi enqueue folder/path.
- [~] Cancel current process; pause/resume từng item để phase sau.
- [~] Retry bằng enqueue lại; nút retry riêng để phase sau.
- [~] Overwrite hiện theo scp mặc định; rule Ask/Skip/Newer để phase sau.
- [~] Có status/progress % cơ bản queued/running/done; bytes/speed/ETA realtime để phase sau.

---

# Phase SSH-08 — Remote File Editor

## Mục tiêu

Sửa file server như WinSCP.

## UI

```txt
Remote Editor
File: /etc/caddy/Caddyfile
[Save] [Save As] [Reload] [Diff] [Backup before save]
```

## Checklist

- [ ] Click file text để mở editor.
- [ ] Detect text/binary.
- [ ] Backup file trước khi save.
- [ ] Save lại qua SFTP.
- [ ] Reload nếu remote file đổi.
- [ ] Basic syntax mode: shell, json, yaml, caddyfile, nginx, env.

---

# Phase SSH-09 — Key Manager giống PuTTYgen

## Mục tiêu

Tạo/quản lý SSH key ngay trong ConfigTool.

## File thêm

```txt
Services/SshKeyService.cs
Models/SshKeyDtos.cs
```

## Checklist

- [ ] Generate key: RSA / ECDSA / Ed25519 nếu lib hỗ trợ.
- [ ] Import private key.
- [ ] Convert/import OpenSSH key.
- [ ] Copy public key cho `authorized_keys`.
- [ ] Gắn key vào profile.
- [ ] Đổi passphrase.
- [ ] Xóa key.
- [ ] Install public key to server bằng password một lần.

---

# Phase SSH-10 — Tunnels giống PuTTY Port Forwarding

## Mục tiêu

Làm SSH tunnel để mở MySQL, web local, SOCKS proxy.

## File thêm

```txt
Services/SshTunnelService.cs
Models/SshTunnelDtos.cs
```

## Tunnel types

```txt
Local Forward:
localhost:3307 -> remote 127.0.0.1:3306

Remote Forward:
remote 0.0.0.0:8080 -> local 127.0.0.1:5000

Dynamic SOCKS:
localhost:1080 -> SSH SOCKS proxy
```

## Checklist

- [ ] Start/stop tunnel.
- [ ] Hiện port đang nghe.
- [ ] Báo lỗi nếu port bị chiếm.
- [ ] Auto start tunnel theo profile nếu bật.

---

# Phase SSH-11 — Sync / Deploy giống WinSCP Synchronize

## Mục tiêu

Biến SSH tab thành công cụ deploy dự án.

## File thêm

```txt
Services/SshDeployService.cs
Services/SftpSyncService.cs
Models/SshDeployDtos.cs
```

## UI

```txt
Sync / Deploy
Job name:
Profile:
Local path:
Remote path:
Mode: Upload changed only / Mirror / Download / Two-way
Exclude:
Before commands:
After commands:
[Dry Run] [Run Deploy]
```

## Exclude mặc định

```txt
bin/
obj/
.git/
.vs/
logs/
temp/
*.user
*.suo
```

## Checklist

- [ ] Dry-run trước khi chạy.
- [ ] So sánh local/remote theo size/time/hash nếu có.
- [ ] Hiện danh sách upload/update/delete.
- [ ] Chạy before commands.
- [ ] Sync file.
- [ ] Chạy after commands.
- [ ] Ghi deploy log.

---

# Phase SSH-12 — Script Runner + Logs + Import/Export

## Mục tiêu

Hoàn thiện thành bộ quản trị server tiện dùng.

## Script Runner

```txt
Scripts
[+ New Script] [Run] [Run selected profile]
```

Script mẫu:

```txt
Restart Caddy
Restart App
Check disk
Check RAM
Tail logs
Update Ubuntu
Run dotnet app
```

Biến script:

```txt
{{remotePath}}
{{serviceName}}
{{domain}}
{{port}}
{{profileName}}
```

## Logs

```txt
Logs
- connection.log
- terminal.log
- transfer.log
- deploy.log
- tunnel.log
- key.log
- ssh-server.log
```

## Import / Export

- [ ] Export profiles không kèm secret.
- [ ] Export SSH Server config không kèm password.
- [ ] Export scripts.
- [ ] Export deploy jobs.
- [ ] Import lại từ JSON.

---

# 3. Thứ tự làm tối ưu

Thứ tự nên làm:

```txt
SSH-00: UI shell / route / nav
SSH-SERVER-01: SSH Server local config + folder dialog + start/stop
SSH-01: Profile CRUD
SSH-02: Secret Vault
SSH-03: SSH Core
SSH-04: Terminal basic
SSH-06: SFTP basic
SSH-07: Transfer Queue
SSH-08: Remote Editor
SSH-09: Key Manager
SSH-10: Tunnel Manager
SSH-11: Sync / Deploy
SSH-05: Terminal advanced
SSH-SERVER-02: SSH Server users/permission/folder mapping
SSH-12: Script / Logs / Import Export
```

Lý do:

```txt
- SSH Server cần config/folder dialog sớm vì user yêu cầu riêng.
- Profile + Secret + SSH Core là nền cho Terminal, SFTP, Tunnel, Sync.
- SFTP basic nên có trước Key/Tunnel nâng cao để dùng được sớm như WinSCP mini.
```

---

# 4. Bản MVP cần hoàn thành trước

```txt
MVP-1: /ssh page + tab nhỏ
MVP-2: SSH Server tab: ip, port, user, pass, root folder dialog, save/start/stop
MVP-3: Profile CRUD
MVP-4: Save password/key path an toàn
MVP-5: Test SSH
MVP-6: Terminal realtime
MVP-7: SFTP list/upload/download/delete/rename
MVP-8: Transfer progress
MVP-9: Deploy local -> remote
```

Sau MVP này ConfigTool đã có:

```txt
- PuTTY mini: mở terminal SSH.
- WinSCP mini: upload/download file.
- PuTTYgen mini: quản lý key cơ bản.
- SSH Server mini: mở SFTP/SSH local từ thư mục đã chọn.
- Deploy tool: đẩy publish folder lên VPS.
```

---

# 5. Map chức năng PuTTY / WinSCP / PuTTYgen / SSH Server

| Gốc | Trong ConfigTool |
|---|---|
| PuTTY Session | SSH Profiles |
| PuTTY Terminal | Terminal tab |
| PuTTY Logging | Logs tab |
| PuTTY SSH Auth | Profile Auth + Keys |
| PuTTY Tunnels | Tunnels tab |
| PuTTY Proxy | Profile Advanced |
| PuTTYgen | Keys tab |
| WinSCP Site Manager | SSH Profiles |
| WinSCP Commander UI | SFTP Files 2 panel |
| WinSCP Transfer Queue | Transfer Queue tab |
| WinSCP Synchronize | Sync / Deploy tab |
| WinSCP Remote Edit | Remote Editor |
| WinSCP Scripting | Scripts + Deploy Jobs |
| Local SSH Server | SSH Server tab |
| Local SFTP Root | SSH Server root folder dialog |

---

# 6. Cấu trúc file dự kiến sau khi hoàn thành

```txt
Components/
└─ Pages/
   └─ SshAdmin.razor

Models/
├─ SshDtos.cs
├─ SftpDtos.cs
├─ SshKeyDtos.cs
├─ SshTunnelDtos.cs
├─ SshDeployDtos.cs
├─ LocalSshServerDtos.cs
└─ SshSecretDtos.cs

Services/
├─ SshConfigService.cs
├─ SshSecretService.cs
├─ SshConnectionFactory.cs
├─ SshSessionRegistry.cs
├─ SshCommandService.cs
├─ SshTerminalService.cs
├─ SftpFileService.cs
├─ SftpTransferQueueService.cs
├─ SshKeyService.cs
├─ SshTunnelService.cs
├─ SftpSyncService.cs
├─ SshDeployService.cs
├─ SshLogService.cs
├─ LocalSshServerConfigService.cs
├─ LocalSshServerHostService.cs
├─ LocalSshServerRuntimeService.cs
└─ LocalSshServerLogService.cs

wwwroot/
└─ vendor/
   ├─ configtool-ssh.js
   └─ configtool-ssh-terminal.js

config runtime:
<AppStartup>/config/sshconfig.json
<AppStartup>/config/ssh-server.json
<AppStartup>/config/credential-vault.json
```

---

# 7. Ghi chú thư viện cần nghiên cứu khi code

- SSH client/SFTP: `SSH.NET` hoặc library tương đương.
- Terminal UI: frontend terminal dạng `xterm.js` hoặc textarea terminal tạm ở MVP.
- SSH Server local: cần chọn thư viện .NET hỗ trợ SSH/SFTP server hoặc tự host SFTP server nếu library phù hợp. Nếu chưa ổn định, MVP có thể ưu tiên SFTP server trước, shell server bật sau.
- Mã hóa secret: Windows DPAPI cho target Windows; sau này nếu cross-platform thì thêm master password + AES-GCM.

---

# 8. Definition of Done cho Tab SSH

Một phase chỉ được tick done khi có đủ:

- [ ] Model/DTO.
- [ ] Service thật, không phải UI giả.
- [ ] UI thao tác được.
- [ ] Lỗi báo mềm.
- [ ] Log có thông tin đủ debug.
- [ ] Không lưu secret thô.
- [ ] Không làm app crash khi disconnect/cancel.
- [ ] Đã đăng ký DI ở đúng nơi nếu service dùng qua SignalR local server.

---

## Patch SSH-08 đến SSH-12 đã triển khai trong bản này

### Phase SSH-08 — Remote File Editor

- [x] Thêm tab `Editor` trong `/ssh`.
- [x] Load remote file text qua SSH command.
- [x] Save remote file qua temp file + `scp` + `mv`.
- [x] Có tùy chọn backup trước khi save: `.bak.yyyyMMddHHmmss`.
- [x] Có diff cơ bản giữa bản đã load và bản đang sửa.
- [x] Có nút dùng remote file đang chọn từ tab Files.

### Phase SSH-09 — Key Manager giống PuTTYgen

- [x] Thêm tab `Keys`.
- [x] Generate key bằng `ssh-keygen` với Ed25519/RSA/ECDSA.
- [x] Import private key vào thư mục `config/sshkeys`.
- [x] Sinh/read public key.
- [x] Lấy fingerprint bằng `ssh-keygen -lf`.
- [x] Gắn key vào SSH profile.
- [x] Install public key vào `~/.ssh/authorized_keys` trên server.

### Phase SSH-10 — Tunnels giống PuTTY Port Forwarding

- [x] Thêm tab `Tunnels`.
- [x] Local forward `-L`.
- [x] Remote forward `-R`.
- [x] Dynamic SOCKS `-D`.
- [x] Start/Stop/Stop all tunnel.
- [x] Theo dõi PID/status tunnel đang chạy.

### Phase SSH-11 — Sync / Deploy giống WinSCP Synchronize

- [x] Thêm tab `Sync / Deploy`.
- [x] CRUD sync/deploy job trong `sshconfig.json`.
- [x] Local path + remote path + mode + exclude.
- [x] Before commands / After commands.
- [x] Dry-run so sánh file local với metadata remote.
- [x] Upload changed/update qua transfer queue.
- [x] Mirror mode có delete remote file không còn trong local.

### Phase SSH-12 — Script Runner + Logs + Import/Export

- [x] Thêm tab `Scripts`.
- [x] Lưu/rerun script preset.
- [x] Biến script: `{{profileName}}`, `{{host}}`, `{{port}}`, `{{user}}`, `{{remotePath}}`, `{{localPath}}`.
- [x] Thêm tab `Logs` xem/xóa log SSH.
- [x] Thêm tab `Settings` export/import JSON.
- [x] Export không kèm password/private key passphrase.

### Ghi chú kỹ thuật

- Terminal/SFTP/Editor/Keys/Tunnel/Sync hiện dùng OpenSSH CLI (`ssh`, `scp`, `ssh-keygen`) để tránh nhúng thư viện nặng ngay lập tức.
- Password auth vẫn không truyền password vào process argument; nên dùng agent/private key.
- `scp`/`ssh` cần có trong PATH của Windows.
- Tunnel là process nền `ssh -N ...`; app có thể stop bằng Kill process tree.

## Patch SSH UI Merge + Modern Explorer

### SSH-MERGE-01 — Gộp Terminal + SFTP + Queue vào tab Kết nối
- Thay tab riêng `Terminal`, `SFTP Files`, `Queue` bằng tab `Kết nối`.
- Mỗi connection workspace có profile riêng, local path, remote path, selected item, terminal command/output riêng.
- Có thể mở nhiều tab kết nối cùng lúc như WinSCP: mỗi tab trỏ tới một SSH profile/tài khoản khác nhau.
- Chuyển tab sẽ lưu/khôi phục trạng thái Terminal + SFTP tương ứng.

### SSH-EXPLORER-02 — Explorer hiện đại hơn
- Thêm menu chuột phải cho local/remote item.
- Thêm kéo thả Local -> Remote để enqueue upload.
- Thêm kéo thả Remote -> Local để enqueue download.
- Thêm folder dialog chọn local path.
- Thêm mở remote file thẳng sang Remote Editor.
- Thêm CSS context menu kiểu Windows Explorer/WinSCP.

### SSH-FIX-PORT-01 — Sửa lỗi SCP port
- `ssh.exe` dùng `-p port`.
- `scp.exe` phải dùng `-P port`.
- Đã tách `AppendCommonSshOptions` và `AppendCommonScpOptions` để tránh lỗi `banner exchange: Connection to UNKNOWN port -1: Connection refused`.


## Patch note — SSH connection compact layout

- Tab `Kết nối` đã chuyển sang layout gọn theo từng session: cột trái là Terminal, cột phải là SFTP Explorer.
- Mỗi session tab vẫn giữ profile/local path/remote path/terminal output riêng.
- `Log Terminal` và `Log SFTP / Transfer Queue` có nút thu nhỏ/mở rộng, mặc định thu nhỏ cho workspace mới.
- SFTP Explorer vẫn giữ chuột phải, kéo thả Local -> Remote để upload và Remote -> Local để download.

## Patch note — SSH dashboard + compact all tabs

- Trang `Tổng quan` đã chuyển từ checklist phase sang dashboard trạng thái SSH thật hơn.
- Dashboard hiển thị trạng thái liên quan SSH:
  - Local SSH Server running/stopped, endpoint, mode.
  - Số profile, profile đang chọn.
  - Số session tab đang mở.
  - Transfer Queue active/done/error.
  - Tunnel running/rule.
  - Số key, sync/deploy job, script preset.
  - Log gần đây và số cảnh báo/lỗi.
- Thêm quick actions ở Tổng quan để nhảy nhanh sang Server, Profiles, Kết nối, Editor, Tunnels, Sync/Deploy.
- Các tab SSH còn lại được đồng bộ layout 2 cột gọn hơn bằng `ssh-split-tab`:
  - SSH Server config/users.
  - Profiles.
  - Editor.
  - Keys.
  - Tunnels.
  - Sync / Deploy.
  - Scripts.
  - Logs.
  - Settings.
- CSS mới làm card bo góc, spacing gọn, form label nhỏ hơn, danh sách/log/table dễ nhìn hơn và responsive về 1 cột trên màn hình nhỏ.

## Patch note — SSH PuTTY terminal + config tab + SFTP logic check

- Gom các tab cấu hình SSH vào tab lớn `Cấu hình`, bên trong chia menu dọc nhỏ: SSH Server, Profiles, Keys, Tunnels, Sync/Deploy, Scripts, Logs, Settings.
- Tab `Kết nối` giữ layout mỗi session 2 cột: Terminal bên trái, SFTP Explorer bên phải.
- Terminal nâng cấp từ command runner sang interactive process session dùng `ssh.exe -tt`, có Connect/Disconnect/Ctrl+C/Send/Refresh output, nhiều session theo workspace.
- SFTP client đổi thao tác list/mkdir/rename/chmod/transfer queue sang `sftp.exe` batch thay vì giả lập hoàn toàn qua SSH command. Recursive delete vẫn dùng SSH fallback vì OpenSSH `sftp` không hỗ trợ rm -rf.
- Các form nhập path local có nút folder dialog: connection local path, profile local start path, sync/deploy local path.
- SSH Server nội bộ được ghi rõ là diagnostic TCP listener, chưa phải SSH/SFTP protocol server thật. Client SSH/SFTP dùng OpenSSH client (`ssh.exe`, `sftp.exe`) và hoạt động tốt nhất với SSH Agent/private key.

## Patch note — SSH Managed Core không dùng OpenSSH CLI

- Đổi SSH client core sang managed `SSH.NET` package.
- Bỏ phụ thuộc client vào `ssh.exe`, `sftp.exe`, `scp.exe` và `ssh-keygen.exe`.
- `SshCoreService` mới dùng `SshClient`/`SftpClient` trực tiếp:
  - Password auth đọc từ `SshSecretService`.
  - Private key auth đọc file key trực tiếp.
  - `agent` trong UI hiện là fallback tìm key mặc định `.ssh/id_ed25519`, `.ssh/id_ecdsa`, `.ssh/id_rsa` vì không còn gọi OpenSSH agent.
- Terminal trong tab `Kết nối` chuyển sang shell stream thật:
  - `SshTerminalSessionService` dùng `SshClient.CreateShellStream("xterm-256color", ...)`.
  - Gửi input/Ctrl+C trực tiếp vào shell stream.
  - Màn hình terminal luôn hiển thị như terminal thật; `History / terminal logs` mặc định thu nhỏ.
- SFTP Explorer/Queue/Remote Editor chuyển sang managed SFTP:
  - List folder, mkdir, rename, chmod, delete bằng `SftpClient`.
  - Upload/download queue bằng `SftpClient.UploadFile`/`DownloadFile` có progress callback.
  - Remote Editor load/save bằng SFTP stream, có backup `.bak.yyyyMMddHHmmss`.
- Tunnels chuyển sang managed port forwarding:
  - Local forward dùng `ForwardedPortLocal`.
  - Remote forward dùng `ForwardedPortRemote`.
  - Dynamic SOCKS dùng `ForwardedPortDynamic`.
- Key Manager không dùng `ssh-keygen`:
  - Generate managed RSA key bằng `System.Security.Cryptography.RSA`.
  - Xuất public key dạng OpenSSH `ssh-rsa`.
  - Import key có thể copy private key và dùng `.pub` nếu có; derive public key tự động cho RSA PEM.
- Lưu ý: SSH/SFTP Server local vẫn là diagnostic TCP listener; SSH/SFTP server protocol thật cần phase riêng nếu muốn thay thế OpenSSH server.


## Patch: SSH Terminal PuTTY-like input + Quick Commands

- Terminal input moved into the terminal screen. Press Enter to send the line into the active managed ShellStream session.
- Terminal output is auto-polled/streamed from SSH.NET ShellStream, so the user no longer needs to press Refresh manually.
- Terminal uses Roboto/Roboto Thin with Unicode-friendly fallbacks.
- History/log remains collapsed by default.
- Added Terminal Quick Commands CRUD: save/edit/delete presets, each preset can contain multiple command lines, and Run sends each line in order with a short configurable delay.
- OpenSSH CLI is still not used for client terminal/SFTP in this patch.

## Patch SSH Terminal / Queue / Editor Fix

- Terminal display cleaned ANSI/control sequences: bracketed paste markers `?[2004h`, clear-screen `[H/[2J`, color/control CSI/OSC are no longer shown as raw text.
- `clear/reset/cls` now clears the managed terminal buffer as well as UI output.
- Transfer Queue adds `Auto run khi có task mới`; drag/drop enqueue can start transfer automatically.
- Transfer Queue now runs in background with UI polling and Stop button.
- Remote/local file double-click opens via configured editor mode.
- Editor mode options: internal ConfigTool editor, Windows default app, external editor path. Remote files are downloaded to temp before opening with external/default editor.

---

# Update SSH-XTERM-WINSCP — Explorer + PuTTY terminal tương tác thật

Đã cập nhật theo phân tích Windows Explorer/WinSCP và PuTTY:

## Terminal PuTTY/xterm
- [x] Thêm vendor `xterm.js` + `addon-fit` local trong `wwwroot/vendor`.
- [x] `index.html` load `xterm.css`, `xterm.js`, `addon-fit.js` trước `configtool-ssh.js`.
- [x] Terminal trong tab Kết nối chuyển từ `<pre>/input/log console` sang host xterm thật.
- [x] `SshTerminalSessionService` giữ raw stream từ `SSH.NET ShellStream`, không clean ANSI cho render chính nữa.
- [x] JS `xterm.onData(...)` gửi raw key về C# để truyền thẳng vào `ShellStream`.
- [x] Hỗ trợ raw key cho tmux/nano/vim/htop: Ctrl+B, Ctrl+C, Ctrl+D, Esc, Tab, arrow keys qua xterm.
- [x] Resize xterm gọi về C#; C# resize PTY bằng reflection best-effort để tương thích nhiều version SSH.NET.
- [x] History/log vẫn là panel phụ thu nhỏ; terminal chính không dùng log console để render.
- [x] Right click terminal: copy selection hoặc paste clipboard; có nút Copy selection/Paste.

## Explorer WinSCP/Windows Explorer
- [x] Local/Remote panel có filter/search tại chỗ.
- [x] Sort theo Name/Type/Size/Modified/Permission, đảo chiều sort, folders-first.
- [x] Toggle hidden/dot files.
- [x] View mode details/list.
- [x] Status bar giống Explorer: path, item count, selected info, phím tắt.
- [x] Keyboard interactions: Enter mở, F2 rename, Delete xóa, Ctrl+C/X/V copy/cut/paste, F5 reload, Alt+Up lên thư mục cha.
- [x] Context menu thêm Properties và Open terminal here cho remote.
- [x] Local context menu thêm Rename/Delete.
- [x] Download progress dùng remote size thật khi SFTP trả được metadata.

## Ghi chú còn lại
- SSH/SFTP client đã là managed SSH.NET.
- SSH Server local vẫn là diagnostic TCP listener, chưa phải SSH/SFTP protocol server thật.
- Multi-select sâu như Windows Explorer (Ctrl-click/Shift-click nhiều item) và conflict dialog Replace/Skip/Keep both vẫn để phase nâng cao.

## Bổ sung SSH Explorer / PuTTY Terminal polish — 2026-06-10

Đã cập nhật tiếp theo phân tích Windows Explorer/WinSCP và PuTTY:

- Terminal:
  - Đổi font terminal sang stack monospace thực tế: Cascadia Mono / Consolas / JetBrains Mono / Noto Sans Mono.
  - Thêm chọn font trong mini settings.
  - Thêm nút Fit gửi lại cols/rows xuống PTY để tmux/vim/nano/htop nhận kích thước tốt hơn.
  - Thêm Zoom/Split terminal mode để tmux có vùng rộng hơn giống PuTTY khi cần.

- Explorer / WinSCP:
  - Multi-select Ctrl-click và Shift-click.
  - Upload/download nhiều item theo selection.
  - Drag/drop Local -> Remote, Remote -> Local, Local -> Local, Remote -> Remote.
  - Drop vào folder row sẽ target đúng folder đó.
  - Queue có conflict rule: Replace / Skip / Keep both / Newer only.
  - CSS toolbar/explorer được làm gọn để tránh nút và hook tràn khỏi panel.

- Dialog native:
  - Mở rộng `IConfigFolderPicker` thêm `PickFileAsync`.
  - Windows dùng native Explorer-style `IFileOpenDialog` cho cả file/folder, không dùng WinForms nhỏ.
  - Thêm file dialog cho upload file local, private key, import key, external editor.

Chưa hoàn tất:

- External drag từ Windows Explorer vào WebView để upload raw file content chưa làm, vì WebView/browser thường không expose full local path an toàn. Nội bộ Local/Remote drag/drop trong ConfigTool đã làm.
- Remote external editor auto-watch/save rồi upload lại server chưa làm.
- SSH/SFTP Server protocol thật vẫn chưa làm; server local vẫn là diagnostic listener.

## Cập nhật SSH Soft Error / External Editor Watch / External Drop

- Thêm soft exception handling cho SSH/SFTP để `SftpPermissionDeniedException: Permission denied` và các lỗi khác không làm crash app.
- Transfer queue chuyển lỗi từng item sang trạng thái `error` thay vì làm chết UI.
- Remote file mở bằng Windows default/external editor có watcher: save file temp sẽ tự upload lại remote bằng SFTP.
- External drag từ Windows Explorer vào WebView đã có best-effort handler. Nếu WebView2 trả được full path thì upload/copy hoạt động; nếu không trả path thì báo lỗi mềm và dùng Open File/Folder dialog.
- SSH/SFTP Server protocol thật vẫn tách phase riêng; Diagnostic TCP Listener chưa phải SSH server thật.

## Bổ sung SSH permission/layout/select patch

- `ListRemoteAsync` đã chuyển sang safe list result để lỗi `Permission denied` khi vào path cấm đọc không làm crash app.
- Thêm thu/gọn cột Terminal/SFTP và Local/Remote Explorer để mở rộng cột còn lại.
- Explorer thêm Ctrl+A chọn tất cả, nút Select all/Clear selection, Ctrl/Shift select, kéo chuột chọn nhiều item và delete nhiều item với lỗi mềm.

---

## Cập nhật SERVER-01 → SERVER-05

### SERVER-01 — Kiểm tra kết nối bằng WinSCP
- SSH Server local đã chuyển từ TCP listener chẩn đoán sang server SSH/SFTP nội bộ.
- Mục tiêu kiểm thử: WinSCP chọn protocol `SFTP`, host theo Bind IP, port theo cấu hình, đăng nhập bằng username/password trong tab Server.
- Server ghi log handshake, thuật toán đã đàm phán, đăng nhập thành công/thất bại và trạng thái mở SFTP subsystem.

### SERVER-02 — Tương thích thuật toán SSH phổ biến
- KEX: `diffie-hellman-group14-sha256`, fallback `diffie-hellman-group14-sha1`.
- Host key: RSA 3072-bit, signature `rsa-sha2-256`, `rsa-sha2-512`, fallback `ssh-rsa`.
- Cipher: `aes128-ctr`, `aes256-ctr`.
- MAC: `hmac-sha2-256`, fallback `hmac-sha1`.

### SERVER-03 — Host key cố định
- Host key được tạo lần đầu và lưu trong `config/ssh-server/host_rsa.key`.
- Khởi động lại app không đổi host key, tránh cảnh báo host key changed trong WinSCP.
- Runtime status hiển thị fingerprint dạng `SHA256:...` để đối chiếu khi client hỏi trust host key.

### SERVER-04 — Password auth ổn định
- Hỗ trợ tài khoản mặc định của server và danh sách user riêng.
- Password được giải mã từ vault nội bộ trước khi xác thực.
- User disabled, sai password hoặc tắt password auth đều bị từ chối mềm và ghi log.

### SERVER-05 — SFTP subsystem v3
- Hỗ trợ SFTP v3 cơ bản cho WinSCP: init/version, realpath, list folder, stat/lstat/fstat, open/read/write/close, mkdir, rmdir, remove, rename, chmod/time best-effort và `posix-rename@openssh.com`.
- Mỗi user bị giới hạn trong root folder riêng, không được thoát sandbox bằng `..` hoặc path tuyệt đối lạ.
- Quyền đọc/ghi/xóa được kiểm tra trước từng thao tác.

### Giới hạn còn lại
- Shell/PTY server-side chưa làm trong phần này.
- Public key auth server-side chưa làm.
- Một số extension nâng cao của SFTP như `statvfs`, `fsync`, symlink/hardlink đang trả `unsupported` để client xử lý mềm.
- Cần test WinSCP thực tế. Nếu client báo lỗi thuật toán, handshake hoặc SFTP packet cụ thể, lấy log WinSCP + log ConfigTool để vá tiếp.

---

## Cập nhật SERVER-11 → SERVER-13 và ổn định SSH Client

### Sửa ổn định SSH Client managed core
- `SshCoreService` không còn phụ thuộc cứng vào đúng một `AuthType` khi profile đã có nhiều dữ liệu xác thực.
- Nếu profile chọn `privateKey` nhưng vẫn có password đã lưu, client thử private key trước rồi fallback password/keyboard-interactive.
- Nếu profile chọn `password` nhưng vẫn có private key, client thử password trước rồi fallback private key.
- Nếu profile chọn `agent`, managed core không gọi OpenSSH agent bên ngoài; thay vào đó dùng private key/password đã lưu trong profile nếu có.
- Thông báo `SshAuthenticationException` được chuẩn hóa lại để người dùng biết cần kiểm tra username, password, private key, passphrase và AuthType.

### SERVER-11 — Trạng thái server thực tế
- Runtime status bổ sung danh sách client đang kết nối.
- Mỗi client hiển thị endpoint, username sau khi xác thực, thời điểm kết nối và thao tác cuối.
- Khi session kết thúc, active count và danh sách client tự cập nhật.

### SERVER-12 — Checklist kiểm thử WinSCP
- Tab Server bổ sung checklist thao tác kiểm thử nhanh: connect SFTP, accept host key, login, list folder, upload/download/rename/delete.
- Audit log tiếp tục ghi handshake, auth, SFTP action và lỗi mềm để đối chiếu với log WinSCP.

### SERVER-13 — Shell/Exec server-side cơ bản
- Server core nhận `pty-req`, `window-change`, `env`, `shell` và `exec` channel request.
- Nếu user có quyền shell và server bật Shell, ConfigTool mở shell process cục bộ.
- Windows dùng `cmd.exe`/`ComSpec`; Linux/macOS dùng `/bin/bash -i` nếu có, fallback `/bin/sh -i`.
- Dữ liệu stdin/stdout/stderr được truyền qua SSH channel data.
- `exec` chạy command một lần, gửi exit-status và đóng channel.
- Đây là shell process cơ bản, chưa phải PTY đầy đủ như OpenSSH/ConPTY. SFTP vẫn là mục tiêu ổn định chính của SSH Server nội bộ.
