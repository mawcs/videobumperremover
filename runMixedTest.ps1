
# --- Edit these for each test case -----------------------------------------
$ClipEpisode        = "D:\Data\dev\git\videobumperremover\test_materials\Caprica\Season 01\Caprica - S01E02 - Rebirth.mkv"
$EpisodesDir         = "D:\Data\dev\git\videobumperremover\test_materials\Caprica\Season 01"
$Region              = "end"          # begin | end
$TotalLengthSeconds  = 3               # full known bumper length
$EdgeBoundarySeconds = 20               # dense zone length (0 = all sparse)
$DenseIntervalSeconds  = 0.1            # sample interval inside the boundary
$SparseIntervalSeconds = 4              # sample interval beyond it
$NegativeDir         = "D:\Data\dev\git\videobumperremover\test_materials\Doctor Who\Season 01"  # "" to skip

# Optional: save this run's output alongside the others for later comparison
$LogPath = "D:\Data\dev\git\videobumperremover\test_materials\phash-vs-dino-$(Get-Date -Format yyyyMMddHHmm).txt"
# -----------------------------------------------------------------------------

$env:BUMPER_CLIP_EPISODE                    = $ClipEpisode
$env:BUMPER_EPISODES_DIR                    = $EpisodesDir
$env:BUMPER_REGION                          = $Region
$env:BUMPER_MIXED_TOTAL_LENGTH_SECONDS      = $TotalLengthSeconds
$env:BUMPER_MIXED_EDGE_BOUNDARY_SECONDS     = $EdgeBoundarySeconds
$env:BUMPER_MIXED_DENSE_INTERVAL_SECONDS    = $DenseIntervalSeconds
$env:BUMPER_MIXED_SPARSE_INTERVAL_SECONDS   = $SparseIntervalSeconds
$env:BUMPER_MIXED_NEGATIVE_DIR              = $NegativeDir

dotnet test VBR.Tests --filter "FullyQualifiedName~VisualBumperMatcherMixedDensityTests" `
    -l "console;verbosity=detailed" 2>&1 | Tee-Object -FilePath $LogPath