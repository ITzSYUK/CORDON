@{
    SchemaVersion = 2
    SourceRepository = 'https://github.com/ModOrganizer2/usvfs'
    SourceRevision = 'c5f7ef2085bd29a3cca2cd57f3aba84dae030ff5'
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
