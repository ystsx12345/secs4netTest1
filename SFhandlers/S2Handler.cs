using Secs4Net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Secs4Net.Item;

namespace secs4netTest1.SFhandlers
{
    internal class S2Handler
    {


        private readonly IReplyContext _reply;

        public S2Handler(IReplyContext replyContext)
        {
            _reply = replyContext;
        }


        public async Task S2F41ReplyAsync(PrimaryMessageWrapper wrapper)
        {

            await _reply.ReplyAsync(wrapper, 2, 42, B(0));


        }




    }
}
