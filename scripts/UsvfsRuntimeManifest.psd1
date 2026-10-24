@{
    SchemaVersion = 2
    SourceDirectory = '.external\usvfs-pr99'
    SourceRepository = 'https://github.com/ModOrganizer2/usvfs'
    SourceRevision = '93fcf7af42523d8da8d1436e88920f900e766596'
    RuntimeVersion = '0.5.7.2'
    SourcePatches = @(
        'scripts\patches\usvfs-pr99-tests.patch'
        'scripts\patches\usvfs-pr99-runtime.patch'
    )
    Files = @(
        @{ Name = 'usvfs_x64.dll'; RelativePath = 'lib\usvfs_x64.dll' }
        @{ Name = 'usvfs_proxy_x64.exe'; RelativePath = 'bin\usvfs_proxy_x64.exe' }
        @{ Name = 'usvfs_x86.dll'; RelativePath = 'lib\usvfs_x86.dll' }
        @{ Name = 'usvfs_proxy_x86.exe'; RelativePath = 'bin\usvfs_proxy_x86.exe' }
    )
}
