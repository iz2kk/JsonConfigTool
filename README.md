# Chuẩn bị
SDK .NET 10:  https://dotnet.microsoft.com/en-us/download/dotnet/10.0

# ConfigTool

ConfigTool là ứng dụng **MAUI Blazor Hybrid chạy Windows** dùng để quản trị file cấu hình JSON và quản trị SQL bằng giao diện hiện đại. Mục tiêu chính là hỗ trợ workflow làm game/Unity: Unity có thể đang ghi file config trong lúc chạy, còn ConfigTool vẫn theo dõi realtime, reload/merge an toàn và hạn chế ghi đè dữ liệu mới từ app khác.

## Tính năng chính

### JSON Config CRUD

- Chọn thư mục config bằng native Windows Open Folder Dialog.
- Quét và đọc tất cả file `*.json` trong thư mục đã chọn.
- Lưu thư mục đã chọn vào:

```txt
<AppStartup>/config/cauhinh.json
```

- Hiển thị mỗi file JSON thành bảng/block dữ liệu.
- CRUD object, array, item, key/field như làm việc với database.
- Block editor cha/con cho object và array, có thu gọn/mở rộng.
- Array hỗ trợ thêm nhanh theo số lượng, thêm theo index, giữ item cũ khi update.
- Nhập nhanh bằng JSON paste hoặc danh sách dấu phẩy, ví dụ:

```txt
item 1, item 2, item 3
```

- Có tìm kiếm nhanh, phân trang, query form và gợi ý key/field có sẵn.
- Tạo file JSON mới bằng giao diện block.
- Theo dõi realtime thay đổi file từ Unity/app khác bằng FileSystemWatcher + background scan.
- Load dữ liệu qua SignalR local server, client Blazor không cần JS custom.

### SQL Admin

- Quản trị nhiều tài khoản kết nối SQL trong file:

```txt
<AppStartup>/config/connect.json
```

- Hỗ trợ nhóm kết nối:
  - MySQL
  - MariaDB
  - SQL Server / MSSQL
- Thêm, sửa, xóa, nhân bản, test kết nối tài khoản SQL.
- Chọn database, bảng, cột, khóa/key bằng giao diện.
- CRUD database, table, column, foreign key và record.
- Query editor chạy qua SignalR.
- Menu chuột phải cho database, bảng, cột, key, foreign key, record và kết quả query.
- Import/export SQL kiểu phpMyAdmin.
- Query có menu chọn nhanh bảng/cột/key để chèn vào dòng lệnh.
- Kết quả SELECT có thể sửa/xóa/thêm record trực tiếp trong bảng kết quả.

### UI

- MAUI Blazor Hybrid, single-page style.
- Bootstrap modal, layout hiện đại, responsive.
- Nav ngang trên cùng.
- Modal rộng khoảng 90% cửa sổ, có thể resize.
- FontAwesome Pro 7.2.0 dùng qua local vendor path.
- Hạn chế JS custom, chỉ dùng JS của Bootstrap khi cần.

## Công nghệ sử dụng

- .NET / MAUI Blazor Hybrid Windows
- Blazor
- SignalR local server
- Bootstrap local vendor
- FontAwesome local vendor
- MySqlConnector cho MySQL/MariaDB
- Microsoft.Data.SqlClient cho SQL Server nếu bật module SQL Server

## Yêu cầu môi trường

- Windows 10/11.
- Visual Studio 2026 hoặc IDE hỗ trợ MAUI/.NET tương ứng.
- .NET SDK đúng version project đang dùng.
- MAUI workload.

Cài workload nếu máy chưa có:

```bat
dotnet workload install maui
```

Hoặc restore workload theo project:

```bat
dotnet workload restore
```

## Cấu trúc thư mục quan trọng

```txt
ConfigTool/
├─ Components/
│  ├─ Layout/                  # Layout Blazor
│  └─ Pages/                   # Trang JSON CRUD, SQL Admin
├─ Hubs/                       # SignalR hub
├─ Models/                     # DTO/model dùng cho JSON + SQL
├─ Services/                   # Service đọc/ghi JSON, watcher, SQL, config
├─ Platforms/Windows/          # Windows folder dialog, window sizing
├─ wwwroot/
│  ├─ app.css
│  └─ vendor/
│     ├─ bootstrap/
│     └─ fontawesome/
├─ Resources/
├─ ConfigTool.csproj
├─ Directory.Build.props
├─ Directory.Build.targets
└─ README.md
```

## Cấu hình vendor Bootstrap và FontAwesome

Project trỏ sẵn asset theo path local:

```txt
wwwroot/vendor/bootstrap/css/bootstrap.min.css
wwwroot/vendor/bootstrap/js/bootstrap.bundle.min.js
wwwroot/vendor/fontawesome/css/all.min.css
```

Nếu dùng FontAwesome Pro 7.2.0, tự copy asset hợp lệ theo license vào:

```txt
wwwroot/vendor/fontawesome/
```

Không nên upload/chia sẻ font hoặc asset Pro nếu license không cho phép.

## Cấu hình JSON tool

File cấu hình chính:

```txt
<AppStartup>/config/cauhinh.json
```

Ví dụ:

```json
{
  "configFolderPath": "D:\\UnityProjects\\AvatarTraining\\Assets\\Configs",
  "signalRPort": 59177,
  "lastFolderSelectedAt": "2026-06-01T10:00:00+07:00",
  "requiredFileNames": [
    "GameCoreConfig.json",
    "GameJsonDatabaseConfig.json",
    "PlantConfig.json",
    "PlantingConfig.json",
    "tilemaps.json"
  ]
}
```

`requiredFileNames` chỉ là danh sách tham khảo. Tool vẫn có thể quét tất cả file `*.json` trong thư mục được chọn.

## Cấu hình nhiều tài khoản SQL

File cấu hình SQL:

```txt
<AppStartup>/config/connect.json
```

Mẫu:

```json
{
  "connect": [
    {
      "id": "local-mysql",
      "name": "Local MySQL root",
      "typeconect": "mysql",
      "host": "127.0.0.1",
      "port": "3306",
      "user": "root",
      "password": "",
      "allownopassword": true,
      "database": "",
      "timeoutseconds": 15
    },
    {
      "id": "local-mariadb",
      "name": "Local MariaDB root",
      "typeconect": "mariadb",
      "host": "127.0.0.1",
      "port": "3306",
      "user": "root",
      "password": "",
      "allownopassword": true,
      "database": "",
      "timeoutseconds": 15
    },
    {
      "id": "local-mssql",
      "name": "Local SQL Server sa",
      "typeconect": "sqlserver",
      "host": "127.0.0.1",
      "port": "1433",
      "user": "sa",
      "password": "",
      "allownopassword": false,
      "database": "master",
      "encrypt": false,
      "trustservercertificate": true,
      "timeoutseconds": 15
    }
  ]
}
```

Giá trị `typeconect` hiện dùng:

```txt
mysql
mariadb
sqlserver
mssql
```

## Chạy project trong Visual Studio

1. Mở solution/project `ConfigTool`.
2. Chọn target Windows.
3. Chạy Debug bằng nút Start.
4. Trong app, bấm **Chọn thư mục config** và trỏ tới thư mục chứa JSON.
5. Nếu dùng SQL Admin, vào tab SQL và thêm tài khoản kết nối.

## Build Debug bằng dòng lệnh

Chạy trong thư mục chứa `ConfigTool.csproj`:

```bat
dotnet restore
dotnet build ConfigTool.csproj -c Debug -f net10.0-windows10.0.19041.0
```

## Build Release dạng folder rời

Khuyến nghị dùng `publish`, không dùng Build thường nếu muốn output là thư mục chạy rời.

```bat
dotnet publish ConfigTool.csproj -c Release -f net10.0-windows10.0.19041.0 -r win-x64 -p:WindowsPackageType=None -p:SelfContained=false -p:PublishSingleFile=false -o bin\Release\ConfigTool-Windows-Folder
```

Nếu project đã có file `publish-folder-release.bat`, có thể chạy trực tiếp:

```bat
publish-folder-release.bat
```

Output:

```txt
bin/Release/ConfigTool-Windows-Folder/
```

## Publish slim output

Project có thể dùng publish profile nếu đã thêm:

```bat
dotnet publish ConfigTool.csproj -p:PublishProfile=ConfigTool-Windows-Slim
```

Bản trim mạnh hơn:

```bat
dotnet publish ConfigTool.csproj -p:PublishProfile=ConfigTool-Windows-Slim-Trimmed
```

Bản trimmed cần test kỹ vì MAUI/Blazor/SQL reflection có thể bị ảnh hưởng.

## Dọn thư mục ngôn ngữ vệ tinh

Có thể giới hạn ngôn ngữ trong `ConfigTool.csproj`:

```xml
<PropertyGroup>
  <SatelliteResourceLanguages>vi;en</SatelliteResourceLanguages>
</PropertyGroup>
```

Sau đó clean lại:

```bat
rmdir /s /q bin
rmdir /s /q obj
dotnet clean
dotnet build
```

## Lỗi thường gặp

### Trùng appicon khi build

Lỗi dạng:

```txt
All image output filenames must be unique: appicon (wwwroot\appicon.png)
```

Cách xử lý:

- Đổi tên `wwwroot/appicon.png` thành `favicon.png`, hoặc
- Remove `wwwroot/**/*.png` khỏi `MauiImage` bằng `Directory.Build.targets`.

Sau đó xóa cache:

```bat
rmdir /s /q bin
rmdir /s /q obj
```

### MySQL access denied

Lỗi dạng:

```txt
Access denied for user 'root'@'localhost' (using password: NO)
```

Kiểm tra lại:

- `user`
- `password`
- `allownopassword`
- quyền đăng nhập theo host
- MySQL/MariaDB service đã bật chưa

### Folder dialog không hiện

Kiểm tra đang chạy target Windows và project dùng implementation trong:

```txt
Platforms/Windows/WindowsConfigFolderPicker.cs
```

## Gợi ý trước khi upload Git

Không nên commit các thư mục/file build:

```txt
bin/
obj/
.vs/
*.user
*.suo
```

Không nên commit thông tin nhạy cảm:

```txt
config/cauhinh.json
config/connect.json
```

Nên commit file mẫu:

```txt
connect.example.json
```

Không nên commit FontAwesome Pro nếu license không cho phép. Có thể để sẵn README hướng dẫn copy asset vào `wwwroot/vendor/fontawesome`.

## Workflow sử dụng nhanh

### JSON

1. Mở app.
2. Chọn thư mục config của Unity.
3. Chọn file JSON.
4. Chọn bảng/block.
5. Tìm kiếm, thêm, sửa, xóa key/item/record.
6. Nếu Unity đổi file khi app đang mở, tool sẽ reload realtime và hạn chế ghi đè dữ liệu mới.

### SQL

1. Vào tab SQL.
2. Thêm tài khoản kết nối.
3. Bấm test kết nối.
4. Chọn database.
5. Chọn bảng/cột/key.
6. CRUD bảng/cột/record hoặc chạy query.
7. Dùng menu chuột phải để copy/paste/cut/export/import/xóa nhanh.

## Ghi chú phát triển

- Ưu tiên SignalR cho load dữ liệu/tìm kiếm/realtime.
- Không thêm JS custom nếu Blazor làm được.
- Bootstrap JS chỉ dùng cho modal/behavior cơ bản nếu cần.
- Khi ghi JSON cần đọc bản mới nhất trước, merge thay đổi rồi mới save để tránh đấu dữ liệu với Unity.
- Query SQL nên bọc soft error, không throw làm crash app.
- Các thao tác nặng nên chạy async/background và không block UI thread.
