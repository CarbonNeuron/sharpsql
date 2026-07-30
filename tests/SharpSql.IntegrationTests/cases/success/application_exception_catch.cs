try
{
    Console.WriteLine("before");
    throw new ApplicationException("application failure");
}
catch (ApplicationException exception)
{
    Console.WriteLine(exception.Message);
}

Console.WriteLine("after");
