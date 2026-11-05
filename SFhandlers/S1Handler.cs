using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Secs4Net;
using static Secs4Net.Item;

namespace secs4netTest1.SFhandlers
{
    internal class S1Handler
    {
        private readonly IReplyContext _reply;

        public S1Handler(IReplyContext replyContext)
        {
            _reply = replyContext;
        }


        public  async Task S1F1ReplyAsync(PrimaryMessageWrapper wrapper)
        {

          await  _reply.ReplyAsync(wrapper, 1, 2, L(A("EQP"), A("1.0.0")));


        }


        public async Task S1F13ReplyAsync(PrimaryMessageWrapper wrapper)
        {

            await _reply.ReplyAsync(wrapper, 1, 14, L(B(0), L(A("EQP"), A("1.0.0"))));


        }

        public async Task S1F15ReplyAsync(PrimaryMessageWrapper wrapper)
        {

            await _reply.ReplyAsync(wrapper, 1, 16, B(0));


        }

        public async Task S1F17ReplyAsync(PrimaryMessageWrapper wrapper)
        {

            await _reply.ReplyAsync(wrapper, 1, 18, B(0));


        }

    }
}
