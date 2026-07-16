CHM 转 PDF
===========

使用方法
--------
1. 双击“CHM转PDF.exe”。
2. 点击“浏览”选择 CHM，或把 CHM 文件直接拖进窗口。
3. 选择 PDF 保存位置。
4. 点击“开始转换”。

运行要求
--------
- Windows 10 或 Windows 11，x64 架构。
- 电脑需要安装 Microsoft Edge（Windows 10/11 通常自带）。
- 不需要安装 .NET、Python、Office 或 Adobe Acrobat。

转换说明
--------
- 工具会优先读取 CHM 自带目录顺序，并在 PDF 中创建章节书签。
- 如果 CHM 没有目录，会按解包后的 HTML 文件路径排序。
- 转换结果尽量保留文字、图片、表格和原网页样式。
- CHM 中依赖 ActiveX、旧版脚本、弹窗或外部网络资源的内容，可能无法完整打印。
- 加密、损坏或非标准 CHM 可能无法转换。

安全说明
--------
- 全程在本机离线处理，不上传文件。
- 临时文件会在转换结束后自动清理。

高级命令行用法
--------------
CHM转PDF.exe --convert "输入.chm" "输出.pdf"

第三方组件
----------
本工具使用 SharpSevenZip / 7-Zip 解包 CHM，使用 PdfSharpCore 合并 PDF。
相关组件的许可证文件包含在程序包内。
