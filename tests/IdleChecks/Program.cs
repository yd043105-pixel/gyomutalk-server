using SchoolMessenger.Desktop;

void Check(bool valid,string label){if(!valid)throw new Exception(label);Console.WriteLine("PASS "+label);}
Check(!IdleActivity.IsAway(TimeSpan.FromMinutes(4.99),5,false),"input below threshold stays online");
Check(IdleActivity.IsAway(TimeSpan.FromMinutes(5),5,false),"exact idle deadline switches to away");
Check(!IdleActivity.IsAway(TimeSpan.Zero,5,false,true),"new mouse or keyboard input restores online");
Check(IdleActivity.IsAway(TimeSpan.Zero,5,true),"Windows lock immediately marks away");
Check(IdleActivity.IsAway(null,5,false,true)&&!IdleActivity.IsAway(null,5,false),"unavailable OS reading preserves prior state");
Check(IdleActivity.Elapsed(50,uint.MaxValue-49)==TimeSpan.FromMilliseconds(100),"Windows 32-bit tick rollover preserves elapsed input time");
if(OperatingSystem.IsWindows())Check(IdleActivity.Read() is not null,"Windows input API is readable without a keyboard or mouse hook");
