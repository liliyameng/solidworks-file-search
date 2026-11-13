SOLIDWORKS File Search 1.3.13 RC88 (Windows x64)

This is a per-machine, framework-dependent MSI. It requires the Microsoft .NET
8 Desktop Runtime and the Microsoft Edge WebView2 Evergreen Runtime already on
the computer. The installer does not download or bundle either runtime.

The SOLIDWORKS task pane is fixed to Library scope. Search results are Windows
Shell items hosted by ExplorerBrowser/IResultsFolder, so normal Shell verbs and
file drag/drop are owned by Windows and SOLIDWORKS rather than a custom list.

Everything is an optional discovery accelerator. When it is absent, SwSearch
uses its built-in discovery and cached SQLite index.

This RC88 package is intended for static build checks. It was not installed and
was not run inside SOLIDWORKS by the packaging workflow; those checks remain for
the user on the target computer.
