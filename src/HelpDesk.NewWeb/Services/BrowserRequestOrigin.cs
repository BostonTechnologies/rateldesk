using Helpdesk.ServiceDefaults.Security;

namespace HelpDesk.NewWeb.Services;

public sealed class BrowserRequestOrigin(PublicIngressIdentity identity)
{
    private string? _address;
    private bool _captured;

    // Routes is a server-rendered interactive root. ASP.NET protects its initial
    // component parameters; browser event callbacks cannot set this address.
    public void Capture(string? address)
    {
        if (_captured)
            return;
        _address = address;
        _captured = true;
    }

    public HttpRequestMessage CreateRequest(HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        identity.AddProof(request, _address);
        return request;
    }
}
