# inspect-dll.ps1 - read-only metadata inspector for .NET assemblies (no loading, no execution).
# Used to verify modding facts instead of guessing: target framework, assembly refs, type names.
#
#   pwsh -File inspect-dll.ps1 -Path <dll> [-Mode summary|typerefs|typedefs|refs] [-Filter <substring>]
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [ValidateSet('summary', 'typerefs', 'typedefs', 'refs', 'members')][string]$Mode = 'summary',
    [string]$Filter = ''
)

$fs = [System.IO.File]::OpenRead($Path)
try {
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($fs)
    $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)

    function TypeName($handle) {
        $t = $md.GetTypeReference($handle)
        $ns = $md.GetString($t.Namespace)
        $nm = $md.GetString($t.Name)
        if ($ns) { "$ns.$nm" } else { $nm }
    }

    # ---- 签名解码：ECMA-335 ----
    # ⚠️ 不能用 BlobReader 传参：它是 struct，PowerShell 按值传递 ⇒ 读指针不前进，
    #    所有参数类型会解成同一个字节。改用"字节数组 + 脚本级游标"。
    $script:_sigBuf = $null
    $script:_sigPos = 0

    function SB_Byte() {
        $v = [int]$script:_sigBuf[$script:_sigPos]
        $script:_sigPos++
        return $v
    }

    function SB_Comp() {
        $b = SB_Byte
        if (($b -band 0x80) -eq 0) { return $b }
        if (($b -band 0x40) -eq 0) {
            $b2 = SB_Byte
            return ((($b -band 0x3F) -shl 8) -bor $b2)
        }
        $b2 = SB_Byte; $b3 = SB_Byte; $b4 = SB_Byte
        return ((($b -band 0x1F) -shl 24) -bor ($b2 -shl 16) -bor ($b3 -shl 8) -bor $b4)
    }

    # 类型索引表（0 基，实测正确；句柄构造在 PowerShell 里不可靠，直接建表查）
    $script:_tdNames = New-Object System.Collections.Generic.List[string]
    foreach ($h in $md.TypeDefinitions) {
        $t = $md.GetTypeDefinition($h); $ns = $md.GetString($t.Namespace); $nm = $md.GetString($t.Name)
        [void]$script:_tdNames.Add($(if ($ns) { "$ns.$nm" } else { $nm }))
    }
    $script:_trNames = New-Object System.Collections.Generic.List[string]
    foreach ($h in $md.TypeReferences) {
        $x = $md.GetTypeReference($h); $ns = $md.GetString($x.Namespace); $nm = $md.GetString($x.Name)
        [void]$script:_trNames.Add($(if ($ns) { "$ns.$nm" } else { $nm }))
    }

    function TokName([int]$tok) {
        $tag = $tok -band 3
        $idx = $tok -shr 2
        try {
            # RID 是 1 基，索引表是 0 基 ⇒ 要减 1（实测校准：LocString / System.Action）
            if ($tag -eq 0) { return $script:_tdNames[$idx - 1] }
            if ($tag -eq 1) { return $script:_trNames[$idx - 1] }
            return "typespec#$idx"
        } catch { return "tok?" }
    }

    function SigType() {
        $et = SB_Byte
        switch ($et) {
            0x01 { return 'void' }      0x02 { return 'bool' }    0x03 { return 'char' }
            0x04 { return 'sbyte' }     0x05 { return 'byte' }    0x06 { return 'int16' }
            0x07 { return 'uint16' }    0x08 { return 'int32' }   0x09 { return 'uint32' }
            0x0a { return 'int64' }     0x0b { return 'uint64' }  0x0c { return 'float32' }
            0x0d { return 'float64' }   0x0e { return 'string' }  0x16 { return 'typedref' }
            0x18 { return 'intptr' }    0x19 { return 'uintptr' } 0x1c { return 'object' }
            0x0f { return (SigType) + '*' }
            0x10 { return 'ref ' + (SigType) }
            0x1d { return (SigType) + '[]' }
            0x14 { $null = SigType; $null = SB_Comp; return 'array[]' }
            0x11 { return (TokName (SB_Comp)) }   # VALUETYPE
            0x12 { return (TokName (SB_Comp)) }   # CLASS
            0x15 { $g = SigType; $n = SB_Comp; $a = @()
                   for ($i = 0; $i -lt $n; $i++) { $a += (SigType) }
                   return "$g<$($a -join ',')>" }
            0x13 { return '!' + (SB_Comp) }
            0x1e { return '!!' + (SB_Comp) }
            default { return ("et=0x{0:X2}" -f $et) }
        }
    }

    function MethodSig($sigHandle) {
        try {
            $script:_sigBuf = $md.GetBlobBytes($sigHandle)
            $script:_sigPos = 0
            $cc = SB_Byte
            if ($cc -band 0x10) { $null = SB_Comp }          # 泛型参数个数
            $pc = SB_Comp
            $ret = SigType
            $ps = @()
            for ($i = 0; $i -lt $pc; $i++) { $ps += (SigType) }
            return "$ret (" + ($ps -join ', ') + ")"
        } catch { return '<sig-err>' }
    }

    switch ($Mode) {
        'summary' {
            $asm = $md.GetAssemblyDefinition()
            "ASSEMBLY      : " + $md.GetString($asm.Name) + "  version=" + $asm.Version
            "RUNTIME       : mvid=" + $asm.Mvid
            # TargetFrameworkAttribute (if present) - custom attribute blob, string is a serialized UTF-8 string
            $tf = '<none>'
            foreach ($h in $asm.GetCustomAttributes()) {
                $ca = $md.GetCustomAttribute($h)
                $ctor = $ca.Constructor
                $ctorName = ''
                if ($ctor.Kind -eq [System.Reflection.Metadata.HandleKind]::MemberReference) {
                    $mr = $md.GetMemberReference([System.Reflection.Metadata.MemberReferenceHandle]$ctor)
                    if ($mr.Parent.Kind -eq [System.Reflection.Metadata.HandleKind]::TypeReference) {
                        $ctorName = TypeName $mr.Parent
                    }
                }
                if ($ctorName -like '*TargetFrameworkAttribute') {
                    $blob = $md.GetBlobBytes($ca.Value)
                    # blob: 01 00 <compressed len> <utf8 bytes> 00 00
                    $len = $blob[2]
                    if ($len -band 0x80) { $len = (($len -band 0x3F) -shl 8) -bor $blob[3]; $start = 4 } else { $start = 3 }
                    $tf = [System.Text.Encoding]::UTF8.GetString($blob, $start, $len)
                }
            }
            "TargetFramework: $tf"
            "AssemblyRefs  :"
            foreach ($h in $md.AssemblyReferences) {
                $r = $md.GetAssemblyReference($h)
                "    " + $md.GetString($r.Name) + "  " + $r.Version
            }
        }
        'refs' {
            foreach ($h in $md.AssemblyReferences) {
                $r = $md.GetAssemblyReference($h)
                $l = $md.GetString($r.Name) + "  " + $r.Version
                if (-not $Filter -or $l -like "*$Filter*") { $l }
            }
        }
        'typedefs' {
            foreach ($h in $md.TypeDefinitions) {
                $t = $md.GetTypeDefinition($h)
                $ns = $md.GetString($t.Namespace); $nm = $md.GetString($t.Name)
                $full = if ($ns) { "$ns.$nm" } else { $nm }
                if (-not $Filter -or $full -like "*$Filter*") { $full }
            }
        }
        'typerefs' {
            foreach ($h in $md.TypeReferences) {
                $full = TypeName $h
                if (-not $Filter -or $full -like "*$Filter*") { $full }
            }
        }
        'members' {
            # 字段与方法名（含 static/public 标记、字段类型）。只读元数据，不加载程序集、不执行任何游戏代码。
            # -Filter '=ExactName' 表示精确匹配（否则按 *substring* 匹配）。
            $exact = $null
            if ($Filter -and $Filter.StartsWith('=')) { $exact = $Filter.Substring(1) }
            foreach ($h in $md.TypeDefinitions) {
                $t = $md.GetTypeDefinition($h)
                $ns = $md.GetString($t.Namespace); $nm = $md.GetString($t.Name)
                $full = if ($ns) { "$ns.$nm" } else { $nm }
                $match = if ($exact) { $full -eq $exact } elseif ($Filter) { $full -like "*$Filter*" } else { $true }
                if ($match) {
                    "=== $full ==="
                    foreach ($fh in $t.GetFields()) {
                        $f = $md.GetFieldDefinition($fh)
                        $st = if ($f.Attributes -band [System.Reflection.FieldAttributes]::Static) { 'static' } else { 'inst  ' }
                        $pv = if ($f.Attributes -band [System.Reflection.FieldAttributes]::Public) { 'public ' } else { 'nonpub ' }
                        # 解字段签名里的类型（走统一的 SigType，能解析 class/valuetype 的 token）
                        $tn = '?'
                        try {
                            $script:_sigBuf = $md.GetBlobBytes($f.Signature)
                            $script:_sigPos = 0
                            $null = SB_Byte          # FIELD (0x06)
                            $tn = SigType
                        }
                        catch { $tn = '<sig-err>' }
                        "  F $st $pv " + $md.GetString($f.Name) + "  : " + $tn
                    }
                    foreach ($mh in $t.GetMethods()) {
                        $m = $md.GetMethodDefinition($mh)
                        $st = if ($m.Attributes -band [System.Reflection.MethodAttributes]::Static) { 'static' } else { 'inst  ' }
                        $pv = if ($m.Attributes -band [System.Reflection.MethodAttributes]::Public) { 'public ' } else { 'nonpub ' }
                        "  M $st $pv " + $md.GetString($m.Name) + "  : " + (MethodSig $m.Signature)
                    }
                }
            }
        }
    }
}
finally { $fs.Dispose() }
