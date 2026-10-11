extern alias HexGame;

using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json.Nodes;
using HexGame::Game.Shared;

namespace Dingler.Game.Campaign;

/// <summary>
/// Lists metadata references made by the installed original HEX InitEncounter method.
/// This does not execute or alter original IL, and cannot locate the throwing offset.
/// </summary>
internal static class CampaignEncounterIlDiagnostics
{
    private static readonly Dictionary<ushort, OpCode> Opcodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .GroupBy(opcode => unchecked((ushort)opcode.Value))
        .ToDictionary(group => group.Key, group => group.First());

    internal static JsonObject Inspect()
    {
        try
        {
            var method = typeof(AuthoritativeSessionBase).GetMethod("InitEncounter",
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            if (method is null)
                return new JsonObject { ["status"] = "InitEncounter was not found through reflection" };
            var bytes = method.GetMethodBody()?.GetILAsByteArray();
            if (bytes is null)
                return new JsonObject { ["status"] = "Original method has no managed IL body" };

            var references = new JsonArray();
            var instructions = 0;
            for (var offset = 0; offset < bytes.Length && instructions < 2048; instructions++)
            {
                var start = offset;
                ushort code = bytes[offset++];
                if (code == 0xFE && offset < bytes.Length)
                    code = (ushort)(0xFE00 | bytes[offset++]);
                if (!Opcodes.TryGetValue(code, out var opcode)) break;
                var size = opcode.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineI or OperandType.ShortInlineVar or OperandType.ShortInlineBrTarget => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField
                        or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
                        or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch when offset + 4 <= bytes.Length =>
                        4 + Math.Clamp(BitConverter.ToInt32(bytes, offset), 0, 4096) * 4,
                    _ => 0
                };
                if (offset + size > bytes.Length) break;
                if (opcode.OperandType is OperandType.InlineField or OperandType.InlineMethod
                    or OperandType.InlineTok or OperandType.InlineType)
                {
                    var token = BitConverter.ToInt32(bytes, offset);
                    string member;
                    try
                    {
                        var resolved = method.Module.ResolveMember(token,
                            method.DeclaringType?.GetGenericArguments(), method.GetGenericArguments());
                        member = resolved is null ? $"metadata token 0x{token:X8}"
                            : $"{resolved.DeclaringType?.FullName}::{resolved}";
                    }
                    catch (Exception ex) { member = $"unresolved token 0x{token:X8} ({ex.GetType().Name})"; }
                    if (references.Count < 320)
                        references.Add(new JsonObject
                        {
                            ["ilOffset"] = $"IL_{start:X4}",
                            ["opcode"] = opcode.Name,
                            ["member"] = member.Length <= 300 ? member : member[..300],
                        });
                }
                offset += size;
            }
            return new JsonObject
            {
                ["status"] = "managed IL metadata read successfully",
                ["declaringType"] = method.DeclaringType?.FullName,
                ["method"] = method.ToString(),
                ["ilLength"] = bytes.Length,
                ["inspectedInstructions"] = instructions,
                ["referencedMembers"] = references,
                ["note"] = "These are metadata references, not the failing instruction. Determine the null operand from disassembly or targeted runtime tracing before altering the original engine."
            };
        }
        catch (Exception ex)
        {
            return new JsonObject { ["status"] = "IL metadata unavailable", ["error"] = ex.ToString() };
        }
    }
}
