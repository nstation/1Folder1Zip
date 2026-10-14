# 第三者コンポーネントのライセンス

1Folder1Zip のソースコードは [MITライセンス](LICENSE) で公開しています。配布用EXEに同梱する.NETランタイムと、その内部の第三者コンポーネントには、それぞれのライセンスが適用されます。

| コンポーネント | 配布ZIP内の告知ファイル |
| --- | --- |
| .NET Runtime | `licenses/DOTNET-RUNTIME-LICENSE.txt` |
| .NET Runtime内の第三者コンポーネント | `licenses/DOTNET-RUNTIME-NOTICES.txt` |
| Windows Desktop / WPF | `licenses/WPF-LICENSE.txt` |

`./build.ps1 -Package` は、ビルドに使用したランタイムのNuGetパックから告知文をそのままコピーします。必要な告知が見つからない場合、配布ZIPの生成は失敗します。

EXEを再配布する場合は、これらのライセンス・告知を含む配布ZIPをそのまま使用してください。

上流プロジェクト: [.NET Runtime](https://github.com/dotnet/runtime)、[WPF](https://github.com/dotnet/wpf)
