1. cd annas-archive-api
dotnet watch run
you should see "Now listening on: http://localhost:5XXX"

2. in a different terminal: cd annas-archive-app
ng serve
https://localhost:4200/api/weatherforecast

annas-archive-util/
├─ annas-archive-util.sln
└─ src/
   ├─ AnnasArchive.Core/
   │  ├─ AnnasArchive.Core.csproj
   │  ├─ Models/
   │  │  ├─ BookDto.cs
   │  │  └─ SearchResponseDto.cs
   │  └─ Services/
   │     └─ AnnaArchiveService.cs
   └─ AnnasArchive.Api/
      ├─ AnnasArchive.Api.csproj
      └─ Program.cs