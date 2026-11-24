using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Delivery.Tests;

// Uses the real SignalR JSON protocol over TestServer WebSockets, without a mocked hub.
internal sealed class SignalRProbe(WebSocket socket):IDisposable
{
    private string pending="";
    private int invocation;
    public async Task Send(object message)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)+"\u001e")),WebSocketMessageType.Text,true,timeout.Token);
    }
    public async Task<JsonElement> Read()
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while(true)
        {
            var boundary=pending.IndexOf('\u001e');
            if(boundary>=0)
            {
                var frame=pending[..boundary];pending=pending[(boundary+1)..];
                using var json=JsonDocument.Parse(frame);var result=json.RootElement.Clone();
                if(result.TryGetProperty("type",out var type)&&type.GetInt32()==6)continue;
                return result;
            }
            var buffer=new byte[16384];
            var received=await socket.ReceiveAsync(new ArraySegment<byte>(buffer),timeout.Token);
            if(received.MessageType==WebSocketMessageType.Close)throw new InvalidOperationException("SignalR closed before the expected response.");
            pending+=Encoding.UTF8.GetString(buffer,0,received.Count);
        }
    }
    public async Task<JsonElement> Invoke(string method,Guid order)
    {
        var id=(++invocation).ToString();await Send(new{type=1,invocationId=id,target=method,arguments=new[]{order}});
        var result=await Read();Assert.Equal(3,result.GetProperty("type").GetInt32());Assert.Equal(id,result.GetProperty("invocationId").GetString());return result;
    }
    public void Dispose(){socket.Abort();socket.Dispose();}
}
