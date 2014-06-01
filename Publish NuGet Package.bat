:: make sure we have a clean release build
msbuild /t:Rebuild /p:Configuration=Release /p:Platform="Any CPU" NVorbis.sln

:: remove any existing nupkg files
del *.nupkg

:: build the nuget packages
.nuget\nuget pack NVorbis.nuspec

:: upload the nuget packages
.nuget\nuget push *.nupkg

:: remove nupkg files after uploading them
del *.nupkg
