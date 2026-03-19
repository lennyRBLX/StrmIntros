using System;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmIntros.Web.Api;
using StrmIntros.Web.Helper;

namespace StrmIntros.Web.Service
{
    [Unauthenticated]
    public class ShortcutMenuService : IService, IRequiresRequest
    {
        private readonly IHttpResultFactory _resultFactory;

        public ShortcutMenuService(IHttpResultFactory resultFactory)
        {
            _resultFactory = resultFactory;
        }

        public IRequest Request { get; set; }

        public object Get(GetStrmIntrosJs request)
        {
            return _resultFactory.GetResult(Request,
                (ReadOnlyMemory<byte>)ShortcutMenuHelper.StrmIntrosJs.GetBuffer(), "application/x-javascript");
        }

        public object Get(GetShortcutMenu request)
        {
            return _resultFactory.GetResult(ShortcutMenuHelper.ModifiedShortcutsString.AsSpan(),
                "application/x-javascript");
        }
    }
}
