sed -i '/case MessageTypes.ProcessListRequest:/,/break;/d' Remex.Host/Handlers/PingPongHandler.cs

sed -i '/case MessageTypes.LayoutUpdate when message.DashboardProfile is not null:/i \
                    case MessageTypes.ProcessListRequest:\n                        var procs = await processMonitorService.GetProcessesAsync();\n                        await MessageSerializer.SendAsync(webSocket, new RemexMessage { Type = MessageTypes.ProcessListSync, ProcessList = procs }, ct);\n                        break;' Remex.Host/Handlers/PingPongHandler.cs
