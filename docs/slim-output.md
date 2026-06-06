# ConfigTool slim output

Patch này chỉ thêm cấu hình build/publish để giảm DLL và thư mục ngôn ngữ vệ tinh, không thay đổi logic CRUD/UI.

## Mặc định

- Giữ satellite resource languages: `vi`, `en`.
- Xóa các thư mục ngôn ngữ vệ tinh phổ biến trong output Release/Publish: `de`, `fr`, `ja`, `ko`, `ru`, `zh-Hans`, ...
- Xóa `.pdb` và `.xml` trong Release/Publish.
- Gỡ `Microsoft.Extensions.Logging.Debug` bằng `Directory.Build.targets` để giảm DLL thừa.
- Chặn `wwwroot` image bị MAUI Resizetizer quét thành `MauiImage`, tránh trùng `appicon`.

## Publish khuyến nghị, an toàn

```bat
dotnet publish ConfigTool.csproj -p:PublishProfile=ConfigTool-Windows-Slim
```

Profile này là framework-dependent, nhỏ hơn self-contained nhưng máy chạy cần có .NET/Windows App SDK runtime tương ứng.

## Publish aggressive trim

```bat
dotnet publish ConfigTool.csproj -p:PublishProfile=ConfigTool-Windows-Slim-Trimmed
```

Profile này bật trimming mức `partial`. Chỉ dùng sau khi test lại CRUD JSON, SignalR realtime, folder dialog và Unity file watcher.

## Tắt cleanup tạm thời

```bat
dotnet publish ConfigTool.csproj -p:PublishProfile=ConfigTool-Windows-Slim -p:ConfigToolSlimOutput=false
```

Hoặc chỉ giữ `.pdb/.xml`:

```bat
dotnet publish ConfigTool.csproj -p:PublishProfile=ConfigTool-Windows-Slim -p:ConfigToolRemoveSymbolsFromRelease=false
```
