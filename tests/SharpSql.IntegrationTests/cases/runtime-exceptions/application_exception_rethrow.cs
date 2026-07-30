// sharpsql-expect-exception: ApplicationException
try
{
    throw new ApplicationException("application failure");
}
catch (ApplicationException)
{
    throw;
}
