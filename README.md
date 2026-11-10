# XenoSuite

A Windows system toolkit by Xeno: live monitor (CPU, memory, GPU, disks, network, processes,
services, connections), disk health and SMART, space usage, file finder, USB tools, recovery
helpers and a security-audit front end. C# .NET 8 WPF host with a WebView2 UI.

## Build

```
dotnet build -c Release -o dist
```

Needs the .NET 8 SDK and the WebView2 runtime (built into Windows 11). Hardware sensors come
from [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor); some
readings need the app to run as administrator.

App data (window placement, settings) is kept in `%APPDATA%\XenoSuite`.
