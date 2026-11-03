# Search regression checks

Run the checks on Windows with the .NET 10 SDK:

```powershell
dotnet run --project Surf2.RegressionTests/Surf2.RegressionTests.csproj
```

The runner checks copied search references, match counts and navigation, search options, invalid and missing matches, the copy icon, and diagram link navigation for local files and database definitions. It loads the shared WPF resources in hidden windows without loading or saving the user's workspace or connecting to a database.

It also checks diagram image replacement: saved-image ordering, thumbnails and scrolling, the browse action with an empty library, Save and Cancel, local file validation, embedded image persistence, and undo restoring the original picture and pasted-image reference.

To also render a representative code window for visual inspection:

```powershell
dotnet run --project Surf2.RegressionTests/Surf2.RegressionTests.csproj -- --preview C:\Temp\surf-search.png
```

For a manual check in Surf, open a code tab, press Ctrl+F, and enter a repeated substring. Check the current/total counter while moving forwards and backwards, and check that markers show matches throughout the scrollbar area. Hover over the copy icon to see **Copy Search Reference**. Copy the reference, paste it into a diagram object's **Link** field, and double-click that object. Its code page should open with the search restored. Repeat with an already-open page, with no matching text, and in both Tabs and Canvas views.
