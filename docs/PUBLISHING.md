# GitHub公開手順

この作業フォルダーは、ローカルGitの `main` ブランチで管理します。EXE、ビルド生成物、テストデータはGitへ含めず、配布物はGitHub Releasesへ添付します。

公開先: [nstation/1Folder1Zip](https://github.com/nstation/1Folder1Zip)。`origin` は `https://github.com/nstation/1Folder1Zip.git` に設定済みです。

## ソースの初回公開

1. コミット用の名前・メールアドレスを確認してから、初回コミットを作成します。
2. 次のコマンドで、初回コミットとpushを行います。接続先の `origin` は設定済みです。

```powershell
git add .
git commit -m "Initial implementation of 1Folder1Zip 1.0.0"
git push -u origin main
```

公開前に `git remote -v` で指定した接続先であることを確認してください。

`git config user.name` と `git config user.email` はコミットに記録されます。公開前に意図した名前・メールアドレスであることを確認し、必要に応じてGitHubの非公開メールアドレスを使用してください。[GitHubのコミット用メール設定](https://docs.github.com/en/account-and-profile/how-tos/email-preferences/setting-your-commit-email-address)

## ビルドと配布

```powershell
.\build.ps1 -Test -Package
```

`artifacts/releases/` に、バージョンを含む配布ZIPと `SHA256SUMS.txt` を生成します。ZIPには、単一EXE、使い方、MITライセンス、同梱.NETランタイムのライセンス・告知を含めます。

GitHubへpushすると、GitHub ActionsがWindows上でビルド・テスト・パッケージ生成を行います。手動実行ではストレステストも選択できます。GitHub側のワークフローは、最初のpush後に実行結果を確認してください。ワークフローはGitHub Releaseを自動公開しません。

## 初回リリース

1. `main` のCI結果と、[検証記録](../VALIDATION.md) の未検証項目を確認します。
2. `CHANGELOG.md` の公開日を確定します。
3. 公開するコミットへ `v1.0.0` タグを付け、GitHubへ送信します。
4. GitHubの「Releases」からリリースを作り、配布ZIPとチェックサムを添付します。必要に応じてドラフトの状態で確認します。

```powershell
git tag -a v1.0.0 -m "1Folder1Zip 1.0.0"
git push origin v1.0.0
```

EXEの配布は、ソースのpush後に上記のリリース手順で行ってください。
