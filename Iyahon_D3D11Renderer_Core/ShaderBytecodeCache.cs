using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using Vortice.D3DCompiler;

#nullable enable
namespace Iyahon_D3D11Renderer_Core;

/// <summary>
/// HLSL ソースのコンパイル済みバイトコードをプロセス全体でキャッシュするヘルパー。
/// 同一エフェクトが複数アイテムに適用されても再コンパイルを発生させない。
/// </summary>
internal static class ShaderBytecodeCache
{
    private static readonly ConcurrentDictionary<string, byte[]> _cache = new();

    public static byte[]? GetOrCompile(
        string cacheKey,
        string source,
        string entryPoint,
        string profile,
        string sourceName = "inline_shader",
        ShaderFlags shaderFlags = ShaderFlags.None)
    {
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        try
        {
            using var blob = Compiler.Compile(
                source,
                entryPoint,
                sourceName,
                Array.Empty<Vortice.Direct3D.ShaderMacro>(),
                null!,
                profile,
                shaderFlags,
                EffectFlags.None);

            if (blob == null)
            {
                Debug.WriteLine($"[Iyahon_D3D11Renderer_Core] ShaderBytecodeCache: {cacheKey} ({entryPoint}/{profile}) returned null blob.");
                return null;
            }

            var bytes = blob.AsBytes().ToArray();
            _cache.TryAdd(cacheKey, bytes);
            return bytes;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Iyahon_D3D11Renderer_Core] ShaderBytecodeCache compile error ({cacheKey} / {entryPoint} / {profile}): {ex.Message}");
            return null;
        }
    }
}
