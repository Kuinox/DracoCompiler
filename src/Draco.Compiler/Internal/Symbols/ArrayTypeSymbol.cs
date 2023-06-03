namespace Draco.Compiler.Internal.Symbols;

internal sealed class ArrayTypeSymbol : TypeSymbol
{
    /// <summary>
    /// The element type this array stores.
    /// </summary>
    public TypeSymbol ElementType { get; }

    /// <summary>
    /// The rank of the array (number of dimensions).
    /// </summary>
    public int Rank { get; }

    public override Symbol? ContainingSymbol => null;

    public ArrayTypeSymbol(TypeSymbol elementType, int rank)
    {
        this.ElementType = elementType;
        this.Rank = rank;
    }

    public override string ToString() => $"[{new string(',', this.Rank - 1)}]{this.ElementType}";
}