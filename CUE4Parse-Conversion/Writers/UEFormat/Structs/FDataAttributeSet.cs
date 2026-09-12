using System;
using System.Collections.Generic;
using CUE4Parse.UE4.Writers;

namespace CUE4Parse_Conversion.Writers.UEFormat.Structs;

public class FDataAttributeSet : ISerializable
{
    private readonly List<FDataAttribute> _attributes = [];

    public void AddAttribute(string name, Action<FDataAttribute> write)
    {
        var attribute = new FDataAttribute(name);
        write(attribute);
        _attributes.Add(attribute);
    }

    /// <summary>
    /// Builds a group of attributes off to the side and commits them only when
    /// the complete callback succeeds.
    /// </summary>
    public void AddAttributes(Action<FDataAttributeSet> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        var staged = new FDataAttributeSet();
        build(staged);
        _attributes.AddRange(staged._attributes);
    }

    public void Serialize(FArchiveWriter Ar) => Ar.WriteArray(_attributes);
}
