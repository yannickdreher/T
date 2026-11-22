# Projekt in VS Code kompilieren

## Voraussetzungen

- [Visual Studio Code](https://code.visualstudio.com/) und optional die Erweiterung **C# Dev Kit**.
- Das [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) für Windows. Die .NET Runtime allein reicht zum Kompilieren nicht aus.
- Eine Internetverbindung für den ersten Download der NuGet-Pakete.

Nach der SDK-Installation VS Code neu starten und im integrierten Terminal prüfen:

```powershell
dotnet --list-sdks
```

Die Ausgabe muss eine SDK-Version `10.0.x` enthalten.

## Kompilieren und starten

1. Den Projektordner oder `T.code-workspace` in VS Code öffnen.
2. Über **Terminal → Neues Terminal** ein Terminal im Projektordner öffnen.
3. Die Solution kompilieren:

   ```powershell
   dotnet build T.slnx
   ```

   Der Befehl stellt die NuGet-Abhängigkeiten bei Bedarf automatisch wieder her und baut beide Projekte.

4. Die Anwendung starten:

   ```powershell
   dotnet run --project T/T.csproj
   ```

Für eine Release-Kompilierung `dotnet build T.slnx -c Release` ausführen. Mit dem C# Dev Kit kann die Anwendung auch über **Ausführen und Debuggen** gestartet werden.

## Windows-Anwendung veröffentlichen

Für eine eigenständig ausführbare Windows-x64-Version:

```powershell
dotnet publish T/T.csproj -c Release -r win-x64 --self-contained -o publish
```

Die Anwendung liegt anschließend unter `publish/T.exe`. Für andere Zielplattformen muss die Runtime-ID entsprechend angepasst werden.
