"""Non-interactive Authenticode signing for locally built Foundation helpers."""
import json
from pathlib import Path
import re
import shutil
import subprocess


def run(tool, digest, verify_only=False, signing_config=None):
    root = Path(__file__).resolve().parents[1]
    tool = tool.resolve(strict=True)
    if not re.fullmatch(r'[a-fA-F0-9]{64}', digest):
        raise ValueError('Expected SHA256 must contain 64 hexadecimal digits')
    script = root / 'support/sign-foundation-artifacts.ps1'
    if not script.is_file():
        script = root / 'scripts/sign-foundation-artifacts.ps1'
    config = (signing_config or root / 'private/signing/capskov-local.json').resolve()
    powershell = shutil.which('pwsh.exe') or shutil.which('powershell.exe')
    if not powershell or not script.is_file() or not config.is_file():
        raise RuntimeError('PowerShell, the Foundation signer and the local certificate configuration are required')
    command = [
        powershell, '-NoProfile', '-NonInteractive', '-File', str(script),
        '-Configuration', str(config), '-TaskTool', '-FilePath', str(tool),
        '-ExpectedSha256', digest,
    ]
    if verify_only:
        command.append('-VerifyOnly')
    result = subprocess.run(command, capture_output=True, text=True, encoding='utf-8-sig',
                            errors='replace', timeout=60)
    if result.returncode:
        raise RuntimeError(result.stderr.strip() or 'Foundation tool signing failed')
    return json.loads(result.stdout)
