namespace Horizon.Graphics;

/// <summary>
/// Where every member of a shader's <c>Params</c> block sits, as the compiler reflected it. There are no loose uniforms
/// in Vulkan, everything a draw sets goes in that one block (see <see cref="Technique"/>), and this is how
/// <c>SetUniform("uModel", ...)</c> knows which bytes of it to write. Slang lays the block out the std140 way, scalars
/// on 4 bytes, float2 on 8, float3 and float4 on 16, every element of an array padded to 16, a float4x4 is four rows
/// of float4, and the whole thing ends on a multiple of 16.
/// </summary>
public sealed class UniformBlockLayout
{
    /// <summary>One member of the block. An array member says how many elements it has and how far apart they are.</summary>
    public readonly record struct Member(string Name, string Type, int Offset, int Size, int ArrayLength, int ArrayStride);

    private readonly Dictionary<string, Member> members = [];

    /// <summary>How many bytes the block is, rounded up to 16.</summary>
    public int Size { get; }

    /// <summary>The members, in the order they are in the shader.</summary>
    public IReadOnlyList<Member> Members { get; }

    /// <summary>A block with nothing in it, for a shader that sets no uniforms.</summary>
    public static UniformBlockLayout Empty { get; } = new([]);

    private UniformBlockLayout(List<Member> list)
    {
        Members = list;
        int end = 0;
        foreach (var member in list)
        {
            members[member.Name] = member;
            end = Math.Max(end, member.Offset + (member.ArrayLength > 0 ? member.ArrayStride * member.ArrayLength : member.Size));
        }

        Size = (end + 15) / 16 * 16;
    }

    public bool TryGet(string name, out Member member) => members.TryGetValue(name, out member);

    /// <summary>A layout out of members the compiler reflected.</summary>
    public static UniformBlockLayout FromMembers(IEnumerable<Member> members) => new([.. members]);
}
