# Third-party components

This project bundles the Microsoft WebView2 .NET SDK assemblies and loader in
`lib/` so that the Windows build can compile without a separate package restore.
The included files report version **1.0.2210.55**:

- `Microsoft.Web.WebView2.Core.dll`
- `Microsoft.Web.WebView2.WinForms.dll`
- `WebView2Loader.dll`

These files are Microsoft components and remain subject to their own license
and notices. Review the license for the matching Microsoft.Web.WebView2 NuGet
package before redistributing modified or repackaged copies. The WebView2
Runtime itself is not included; it is installed separately by the end user if
needed.
