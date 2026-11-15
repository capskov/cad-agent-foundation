import sqlite3, os, sys

path = sys.argv[1]
print("exists", os.path.exists(path), os.path.getsize(path) if os.path.exists(path) else 0)
con = sqlite3.connect(path)
print("--- jobs ---")
for row in con.execute(
    "select request_id,tool_name,phase,effects,verification,write_fence_released,write_fence_finalized,created_at_utc,updated_at_utc from plugin_jobs order by job_order"
):
    print(row)
print("--- fences ---")
for row in con.execute("select * from plugin_write_fences"):
    print(row)
