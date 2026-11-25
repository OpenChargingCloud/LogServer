# LogServer

The log server for charging stations as a [WWCP_Node](https://github.com/OpenChargingCloud/WWCP_Node):
it takes the log files charging stations upload - by HTTP PUT on a port of its
own, and over SFTP on the node's SSH server - keeps each of them with what is
known about how it came, and shows them in its web interface, its JSON API and
its command line.

It is run by [LogServerCLI](https://github.com/OpenChargingCloud/LogServerCLI),
whose README says how it is configured, how charging stations upload to it,
and what its web interface and JSON API offer.

| | |
|---|---|
| `LogServer/LogServerNode.cs` | the node: the `logServer` section, the store, the upload port, the SFTP area |
| `LogServer/LogServerNode.HTTPUploads.cs` | uploads by HTTP PUT, written to the disk as they arrive |
| `LogServer/UploadPortCertificate.cs` | the certificate of the upload port - its own, not the web interface's |
| `LogServer/LogServerNode.Uploaders.cs` | the accounts in the `uploader` role |
| `LogServer/Uploads/` | the store of the uploads and their index |
| `LogServer/HTTPAPI/` | the JSON API below `/api/v1/logServer` |
| `LogServer/CommandLine/` | `logServer`, `uploads` and `uploaders` |
| `LogServer/Frontend/` | the web interface: TypeScript and SCSS, bundled by webpack and embedded |
| `LogServerTests/` | its tests, and WWCP_Node's conformance suite asked of it |

```
dotnet test LogServerTests/LogServerTests.csproj
```

Open Source under the **Affero GPL 3.0 license**.
