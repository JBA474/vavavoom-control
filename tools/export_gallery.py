"""Export the AuraBox Android app's built-in gallery (assets/prepare.db) to gallery.txt for embedding.

Output lines:
  I|<category>|<name>|<100 hex digits>         static picture (raw 50-byte display data)
  A|<name>|<hex>,<hex>,...                     animation, frames reference picture data
"""
import sqlite3
import sys

# DesiginData$GARRELY_TYPE; type 4 rows are animation frames, listed via the animation table instead
CATEGORIES = {0: 'Local', 1: 'Popular', 2: 'Flags', 3: 'Numbers & letters'}

db = sqlite3.connect(sys.argv[1])
out = open(sys.argv[2], 'w', encoding='utf-8', newline='\n')
by_stamp = {}
for stamp, data, name, gtype in db.execute('select time_stamp, data, name, garrely_type from design order by _id'):
    if not data or len(data) != 50:
        continue
    by_stamp[stamp] = data
    if gtype in CATEGORIES:
        out.write(f"I|{CATEGORIES.get(gtype, 'Other %s' % gtype)}|{(name or '').replace('|', '/')}|{data.hex()}\n")
for row in db.execute('select name, data_1, data_2, data_3, data_4, data_5, data_6, data_7, data_8 from animation order by _id'):
    frames = [by_stamp[s].hex() for s in row[1:] if s and s in by_stamp]
    if frames:
        out.write(f"A|{(row[0] or '').replace('|', '/')}|{','.join(frames)}\n")
out.close()
