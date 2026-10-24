@{
    SchemaVersion = 2
    SourceDirectory = '.external\usvfs'
    SourceRepository = 'https://github.com/ModOrganizer2/usvfs'
    SourceRevision = '57f1ea5e6ad13f7435a7af184748e6c1312c5637'
    SourcePatch = 'scripts\patches\usvfs-msvc-pch.patch'
    RuntimeVersion = '0.5.7.2'
    Files = @(
        @{
            Name = 'usvfs_x64.dll'
            RelativePath = 'lib\usvfs_x64.dll'
        }
        @{
            Name = 'usvfs_proxy_x64.exe'
            RelativePath = 'bin\usvfs_proxy_x64.exe'
        }
        @{
            Name = 'usvfs_x86.dll'
            RelativePath = 'lib\usvfs_x86.dll'
        }
        @{
            Name = 'usvfs_proxy_x86.exe'
            RelativePath = 'bin\usvfs_proxy_x86.exe'
        }
    )
}
