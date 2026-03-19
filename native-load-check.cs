using System;
using NativeScreenCapturer;

try
{
    using var capturer = new ScreenCapturer();
    Console.WriteLine("OK");
}
catch (Exception ex)
{
    Console.WriteLine(ex.GetType().FullName);
    Console.WriteLine(ex.Message);
    Console.WriteLine(ex.ToString());
}
