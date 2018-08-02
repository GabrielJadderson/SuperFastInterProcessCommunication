using System;
using System.Threading.Tasks;

namespace Super_Fast_Inter_Process_Communication
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            //new PipeTest();

            new Program().startFastChannel().Wait();



            Console.ReadLine();
        }


        public async Task startFastChannel()
        {
            using (var channel = DemoServer())
                await DemoClient();
        }

        public IDisposable DemoServer() => new FastChannel("test", true, GetType().Module);

        public async Task DemoClient()
        {
            using (var channel = new FastChannel("test", false, GetType().Module))
            {
                Proxy<Foo> fooProxy = await channel.Activate<Foo>();

                int remoteProcessID = await fooProxy.Eval(foo => foo.ProcessID);
                Console.WriteLine("Remote Process ID: " + remoteProcessID);

                int sum = await fooProxy.Eval(foo => foo.Add(2, 2));
                Console.WriteLine("Sum " + sum);

                int sum2 = await fooProxy.Eval(foo => foo.AddAsync(3, 3));
                Console.WriteLine("Sum " + sum2);

                #region Marshaling

                Proxy<Bar> barProxy = await channel.Activate<Bar>();
                Proxy<Foo> fooProxy2 = await barProxy.Eval(b => b.GetFoo());

                var dx = (await fooProxy2.Eval(foo => foo.Add(2, 2))).ToString();
                Console.WriteLine(dx);

                await barProxy.Run(b => b.PrintFoo(fooProxy2));
                await barProxy.Run(b => b.PrintFoo(new Foo()));

                #endregion Marshaling
            }
        }
    }
}