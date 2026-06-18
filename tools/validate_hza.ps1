param([string]$path)

$bytes = [System.IO.File]::ReadAllBytes($path)
$ms = New-Object System.IO.MemoryStream(,$bytes)
$br = New-Object System.IO.BinaryReader($ms)

function FNV1a([string]$s) {
    [uint32]$hash = 0x811c9dc5
    $enc = [System.Text.Encoding]::UTF8.GetBytes($s)
    foreach ($b in $enc) {
        $hash = $hash -bxor $b
        $hash = [uint32](([uint64]$hash * 0x01000193) -band 0xFFFFFFFF)
    }
    return $hash
}

Write-Host "File: $path ($($bytes.Length) bytes)"
Write-Host "---"

# Header
$magic = $br.ReadUInt64()
$assetSize = $br.ReadUInt64()
$assetTypeHash = $br.ReadUInt64()
$version = $br.ReadUInt32()
$pad0 = $br.ReadUInt32()

$expectedHash = FNV1a "ModelBlobAsset"
Write-Host "magic: 0x$($magic.ToString('X16')) (expect 0x0054455353414800)"
Write-Host "asset_size: $assetSize (file: $($bytes.Length))"
Write-Host "asset_type_hash: 0x$($assetTypeHash.ToString('X8')) (expect 0x$($expectedHash.ToString('X8'))) match=$($assetTypeHash -eq $expectedHash)"
Write-Host "version: $version"

# deps BlobPtr
$dOff = $br.ReadUInt32(); $dSize = $br.ReadUInt32(); $dTS = $br.ReadUInt32(); $dTH = $br.ReadUInt32()
Write-Host "deps: offset=$dOff size=$dSize type_size=$dTS"

# mesh_count, joint_count
$meshCount = $br.ReadUInt32()
$jointCount = $br.ReadUInt32()
Write-Host "mesh_count: $meshCount, joint_count: $jointCount"

# skip armature_transform (64 bytes)
$null = $br.ReadBytes(64)

# meshes BlobPtr
$mOff = $br.ReadUInt32(); $mSize = $br.ReadUInt32(); $mTS = $br.ReadUInt32(); $mTH = $br.ReadUInt32()
Write-Host "meshes: offset=$mOff size=$mSize type_size=$mTS"

# joints BlobPtr
$null = $br.ReadBytes(16)
# joint_names BlobPtr
$null = $br.ReadBytes(16)

$pos = $ms.Position
Write-Host "---"
Write-Host "ModelBlobAsset ends at offset: $pos (expect 168)"

# MeshBlobAsset
$meshBase = $pos
$nameLen = $br.ReadUInt32(); $nameOff = $br.ReadUInt32()
$matLen = $br.ReadUInt32(); $matOff = $br.ReadUInt32()
$idxFmt = $br.ReadUInt32()
$idxCount = $br.ReadUInt32()
$vertCount = $br.ReadUInt32()
$aMinX = $br.ReadSingle(); $aMinY = $br.ReadSingle(); $aMinZ = $br.ReadSingle()
$aMaxX = $br.ReadSingle(); $aMaxY = $br.ReadSingle(); $aMaxZ = $br.ReadSingle()

Write-Host "index_format: $idxFmt (0=U16), index_count: $idxCount, vertex_count: $vertCount"
Write-Host "aabb_min: ($aMinX, $aMinY, $aMinZ)"
Write-Host "aabb_max: ($aMaxX, $aMaxY, $aMaxZ)"

# Read name string
$savedPos = $ms.Position
$ms.Position = $meshBase + $nameOff
$nameBytes = $br.ReadBytes($nameLen)
$meshName = [System.Text.Encoding]::UTF8.GetString($nameBytes)
Write-Host "mesh name: '$meshName' (len=$nameLen, offset=$nameOff)"
$ms.Position = $savedPos

# Read remaining BlobPtrs
$iOff = $br.ReadUInt32(); $iSize = $br.ReadUInt32(); $iTS = $br.ReadUInt32(); $iTH = $br.ReadUInt32()
Write-Host "indices BlobPtr: offset=$iOff size=$iSize type_size=$iTS"
$pOff = $br.ReadUInt32(); $pSize = $br.ReadUInt32(); $pTS = $br.ReadUInt32(); $pTH = $br.ReadUInt32()
Write-Host "positions BlobPtr: offset=$pOff size=$pSize type_size=$pTS"
$nOff = $br.ReadUInt32(); $nSize = $br.ReadUInt32(); $nTS = $br.ReadUInt32(); $nTH = $br.ReadUInt32()
Write-Host "normals BlobPtr: offset=$nOff size=$nSize type_size=$nTS"
$tOff = $br.ReadUInt32(); $tSize = $br.ReadUInt32(); $tTS = $br.ReadUInt32(); $tTH = $br.ReadUInt32()
Write-Host "tangents BlobPtr: offset=$tOff size=$tSize type_size=$tTS"
$uOff = $br.ReadUInt32(); $uSize = $br.ReadUInt32(); $uTS = $br.ReadUInt32(); $uTH = $br.ReadUInt32()
Write-Host "uvs BlobPtr: offset=$uOff size=$uSize type_size=$uTS"

$hasSkin = $br.ReadUInt32()
Write-Host "has_skin: $hasSkin"

$meshEnd = $ms.Position + 32  # skip 2 empty BlobPtrs
Write-Host "MeshBlobAsset ends at offset: $meshEnd (size=$($meshEnd - $meshBase), expect 172)"

# Verify some vertex data
$ms.Position = $meshBase + $pOff
Write-Host "---"
Write-Host "First 3 vertex positions:"
for ($v = 0; $v -lt [Math]::Min(3, $vertCount); $v++) {
    $x = $br.ReadSingle(); $y = $br.ReadSingle(); $z = $br.ReadSingle()
    Write-Host "  v$v`: ($x, $y, $z)"
}

$br.Close()
$ms.Close()
