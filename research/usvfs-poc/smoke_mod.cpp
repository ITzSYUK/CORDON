extern "C" __declspec(dllimport) int baseValue();

extern "C" __declspec(dllexport) int modValue()
{
  return baseValue() + 37;
}
