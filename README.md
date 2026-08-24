# Hexoer

Avalonia 桌面版 **Hexo 一站式工具**，讓你用 GUI 完成：

- 一鍵建立 / 檢查 Hexo 環境（Node、npm、Git、專案初始化）
- 編輯站點 `_config.yml`
- 一鍵安裝並啟用主題（NexT、Butterfly、Fluid…）
- 編輯主題設定檔
- 編輯 Markdown 文章（`source/_posts`、`_drafts`）+ **即時預覽**
- **本機 `hexo server` 預覽**（啟動 / 停止 / 開啟瀏覽器）
- 設定 `hexo-deployer-git`、一鍵部署 GitHub Pages，並查詢 Pages 狀態

## 系統需求

- Windows 10/11（x64）建議；亦可在 Linux / macOS 開發執行
- [.NET 10 SDK](https://dotnet.microsoft.com/)（開發用；安裝包為 self-contained 可不裝 SDK）
- [Node.js](https://nodejs.org/) + npm
- [Git](https://git-scm.com/)

## 建置與執行

```bash
dotnet restore
dotnet build
dotnet run --project src/Hexoer
```

## 使用流程建議

1. **環境設定**：選擇空資料夾 →「一鍵建立」初始化 Hexo 並 `npm install`；若本機沒有網站但 GitHub Pages 已有，可貼 repository 或 `https://USERNAME.github.io/` →「複製到本機」
2. **站點設定**：填寫 title / author / url 等，或直接編輯完整 YAML
3. **主題 Themes**：安裝 NexT（或其它）並啟用
4. **主題設定**：調整 NexT 等主題的 `_config.yml`
5. **Markdown 內容**：新建 / 編輯文章，右側即時預覽；可「啟動 hexo server」在瀏覽器看完整站點
6. **GitHub Pages**：填 repo → 寫入 deploy 設定 → Deploy → 查詢 Pages 狀態

> 查詢私有 repo 或提高 API 配額時，可在 GitHub Pages 頁面填入 Personal Access Token（僅存在本機 `%AppData%/Hexoer/settings.json`）。

## Windows 安裝包

### 一鍵打包

```powershell
# 參考 Hugoer 流程：發佈 + 打包 release 產物
.\scripts\publish.ps1

# 僅使用舊的打包入口也可以
.\scripts\build-installer.ps1
.\scripts\build-installer.cmd
```

產物目錄：

| 路徑 | 說明 |
|------|------|
| `artifacts/publish/win-x64/` | 可直接執行的 self-contained 發佈檔 |
| `artifacts/releases/` | release 彙整目錄 |
| `artifacts/portable/Hexoer-*-win-x64-portable.zip` | 免安裝壓縮包 |
| `artifacts/installer/Hexoer-Setup-*.exe` | Windows 安裝程式（需本機安裝 [Inno Setup 6](https://jrsoftware.org/isinfo.php)） |
| `artifacts/installer/Install-Hexoer.ps1` | 若無 Inno Setup，自動產生的軟安裝腳本（捷徑 + LocalAppData） |

### 僅發佈

```powershell
.\scripts\publish-windows.ps1
.\scripts\publish-windows.ps1 -SingleFile   # 單檔（較大、啟動略慢）
.\scripts\publish.ps1 -Version 1.1.2 -SingleFile
```

> 安裝包為 **self-contained**，終端使用者不必另外安裝 .NET Runtime。  
> 仍需本機有 Node.js / npm / Git 才能操作 Hexo 專案。

## 專案結構

```
src/Hexoer/
  Models/       # 設定、主題、文章、API 狀態
  Services/     # Process / Hexo / Server / Markdown / Theme / Config / Content / GitHub
  ViewModels/   # MVVM
  Views/        # Avalonia UI
  Styles/       # 全域樣式
installer/      # Inno Setup 腳本
scripts/        # 發佈與安裝包腳本
```

## 授權

MIT
