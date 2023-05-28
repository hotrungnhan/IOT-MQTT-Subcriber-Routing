namespace MQTTnet.AspNetCore.Client.Routing.Lib;

/// <summary>
/// A route constraint that allows the value to be null or parseable as the specified type.
/// </summary>
/// <typeparam name="T">The type to which the value must be parseable.</typeparam>
internal class OptionalTypeRouteConstraint<T> : RouteConstraint
{
    public delegate bool TryParseDelegate(string str, out T result);

    private readonly TryParseDelegate _parser;

    public override string Name { get; }

    public OptionalTypeRouteConstraint(TryParseDelegate parser, string name)
    {
        _parser = parser;
        Name = name;
    }

    public override bool Match(string pathSegment, out object convertedValue)
    {
        // Unset values are set to null in the Parameters object created in the RouteContext. To match this pattern,
        // unset optional parameters are converted to null.
        if (string.IsNullOrEmpty(pathSegment))
        {
            convertedValue = null;
            return true;
        }

        if (_parser(pathSegment, out var result))
        {
            convertedValue = result;
            return true;
        }
        else
        {
            convertedValue = null;
            return false;
        }
    }
}