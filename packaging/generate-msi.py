"""Generate the per-user WiX 5 payload map from a self-contained publish folder."""
from pathlib import Path
import sys, hashlib, uuid, xml.etree.ElementTree as E
source, out, version = Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve(), sys.argv[3]
ns = 'http://wixtoolset.org/schemas/v4/wxs'
E.register_namespace('', ns)
def add(parent, tag, **attrs): return E.SubElement(parent, '{'+ns+'}'+tag, attrs)
def ident(prefix, path): return prefix+hashlib.sha256(str(path).replace('\\','/').lower().encode()).hexdigest()[:24]
w = E.Element('{'+ns+'}Wix')
p = add(w, 'Package', Name='KClevoPanel', Manufacturer='KINDYEAR', Version=version, Language='1033', UpgradeCode='B561223C-89C7-4B89-B5FA-48CAAF706F3C', Scope='perUser', InstallerVersion='500')
add(p, 'MajorUpgrade', DowngradeErrorMessage='A newer KClevoPanel version is already installed.', Schedule='afterInstallInitialize')
add(p, 'MediaTemplate', EmbedCab='yes')
add(p, 'Property', Id='MSIRESTARTMANAGERCONTROL', Value='Disable')
add(p, 'Property', Id='ARPPRODUCTICON', Value='AppIcon')
add(p, 'Icon', Id='AppIcon', SourceFile=str(source/'Assets/Monitor.ico'))
add(p, 'Launch', Condition='Installed OR (VersionNT64 AND VersionNT >= 1000)', Message='Windows 10/11 x64 is required.')
local = add(p, 'StandardDirectory', Id='LocalAppDataFolder')
programs = add(local, 'Directory', Id='UserPrograms', Name='Programs')
app = add(programs, 'Directory', Id='INSTALLFOLDER', Name='KClevoPanel')
feature = add(p, 'Feature', Id='Main', Title='KClevoPanel', Level='1')
dirs = {Path('.'): app}
files = sorted(f for f in source.rglob('*') if f.is_file())
for f in files:
    rel = f.relative_to(source)
    parent = rel.parent
    if parent not in dirs:
        for d in reversed([parent, *parent.parents]):
            if d == Path('.') or d in dirs: continue
            dirs[d] = add(dirs[d.parent], 'Directory', Id=ident('D',d), Name=d.name)
    guid = str(uuid.uuid5(uuid.UUID('B561223C-89C7-4B89-B5FA-48CAAF706F3C'), rel.as_posix().lower()))
    c = add(dirs[parent], 'Component', Id=ident('C',rel), Guid=guid, Bitness='always64')
    file_id = {'KClevoPanel.exe':'MainExe', 'KClevoPanel.FanWorker.exe':'FanWorkerExe'}.get(rel.as_posix(), ident('F',rel))
    add(c, 'File', Id=file_id, Source=str(f), KeyPath='no')
    add(c, 'RegistryValue', Root='HKCU', Key=r'Software\KClevoPanel\Installer\Files', Name=ident('F',rel), Type='string', Value=version, KeyPath='yes')
    add(feature, 'ComponentRef', Id=ident('C',rel))
menu = add(p, 'StandardDirectory', Id='ProgramMenuFolder')
folder = add(menu, 'Directory', Id='AppMenu', Name='KClevoPanel')
c = add(folder, 'Component', Id='MenuShortcut', Guid='*')
add(c, 'Shortcut', Id='StartMenuShortcut', Name='KClevoPanel', Target='[INSTALLFOLDER]KClevoPanel.exe', WorkingDirectory='INSTALLFOLDER', Icon='AppIcon')
add(c, 'RemoveFolder', Id='RemoveAppMenu', On='uninstall')
add(c, 'RegistryValue', Root='HKCU', Key=r'Software\KClevoPanel\Installer', Name='Installed', Type='integer', Value='1', KeyPath='yes')
add(feature, 'ComponentRef', Id='MenuShortcut')
add(p, 'CustomAction', Id='RemoveOwnedStartup', FileRef='FanWorkerExe', ExeCommand='--remove-startup', Execute='immediate', Impersonate='yes', Return='check')
sequence = add(p, 'InstallExecuteSequence')
add(sequence, 'Custom', Action='RemoveOwnedStartup', Before='RemoveFiles', Condition='REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE')
E.indent(w)
out.parent.mkdir(parents=True, exist_ok=True)
E.ElementTree(w).write(out, encoding='utf-8', xml_declaration=True)
print(f'Mapped {len(files)} payload files to MSI components')
