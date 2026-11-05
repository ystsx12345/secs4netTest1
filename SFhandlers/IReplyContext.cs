using Secs4Net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace secs4netTest1.SFhandlers
{
    public interface IReplyContext
    {
         Task ReplyAsync(PrimaryMessageWrapper wrapper, byte s, byte f, Item item);



    }
}
