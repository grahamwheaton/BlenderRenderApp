"""Measure peak Windows process memory of stitching, in a fresh Python process."""
import ctypes
from ctypes import wintypes
import json
import sys
import time
from stitch_tiles import stitch

class Counters(ctypes.Structure):
    _fields_=[('cb',wintypes.DWORD),('PageFaultCount',wintypes.DWORD)]+[(name,ctypes.c_size_t) for name in ('PeakWorkingSetSize','WorkingSetSize','QuotaPeakPagedPoolUsage','QuotaPagedPoolUsage','QuotaPeakNonPagedPoolUsage','QuotaNonPagedPoolUsage','PagefileUsage','PeakPagefileUsage')]

started=time.perf_counter()
stats=stitch(sys.argv[1],sys.argv[2],4)
c=Counters()
c.cb=ctypes.sizeof(c)
kernel=ctypes.WinDLL('kernel32',use_last_error=True)
kernel.GetCurrentProcess.restype=wintypes.HANDLE
psapi=ctypes.WinDLL('psapi',use_last_error=True)
psapi.GetProcessMemoryInfo.argtypes=[wintypes.HANDLE,ctypes.POINTER(Counters),wintypes.DWORD]
if not psapi.GetProcessMemoryInfo(kernel.GetCurrentProcess(),ctypes.byref(c),c.cb):
    raise ctypes.WinError(ctypes.get_last_error())
stats.update(elapsed_seconds=time.perf_counter()-started,peak_working_set_mib=c.PeakWorkingSetSize/1024**2)
print(json.dumps(stats,indent=2))
