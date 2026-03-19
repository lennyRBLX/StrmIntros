using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmIntros.Web.Api
{
    [Route("/{Web}/components/strmintros/strmintros.js", "GET", IsHidden = true)]
    [Unauthenticated]
    public class GetStrmIntrosJs
    {
        public string Web { get; set; }

        public string ResourceName { get; set; }
    }
}
