extern "C" __declspec(dllexport) int baseValue()
{
#ifdef USVFS_SMOKE_OVERRIDE
  return 61;
#else
  return 60;
#endif
}
