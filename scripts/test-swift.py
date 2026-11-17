#!/usr/bin/env python3
"""Run the unchanged Swift suite and expose its failure detail in CI annotations."""
from collections import deque
import os
from pathlib import Path
import subprocess
import sys


def main():
    tail = deque(maxlen=200)
    with subprocess.Popen(['swift', 'test', '--force-resolved-versions'],
                          cwd=Path(__file__).resolve().parents[1],
                          stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                          text=True, encoding='utf-8', errors='replace') as process:
        for line in process.stdout:
            print(line, end='', flush=True)
            tail.append(line)
        code = process.wait()
    if code and os.environ.get('GITHUB_ACTIONS') == 'true':
        message = ''.join(tail)[-60000:]
        escaped = message.replace('%', '%25').replace('\r', '%0D').replace('\n', '%0A')
        print('::error title=Swift verification failed::' + escaped, flush=True)
    return code


if __name__ == '__main__':
    sys.exit(main())
