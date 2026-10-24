// The read scope of the client-file reader (design 0.5): the ONLY module that touches a directory handle or a FileList.
//
// It opens exactly `Data/*.MPQ` (the top level of Data, plus only the NAMES of Data's sub-folders, for the "Data/<locale>"
// check) and `WoW.exe` (its version resource, read later by validate.js). WTF, Interface, Logs and Screenshots are never
// opened or listed: the root is listed once for the names of its entries, Data once, and nothing else. A FileList entry is
// filtered by its relative path before any File is handed on; entries outside the scope are dropped without being counted,
// logged or read.
//
// Both scans return the same shape, so everything downstream is independent of how the folder was picked:
//   {name, kind: 'handle'|'filelist', hasData, archives: [{name, size, lastModified, blob}], dataFolders: [names], exe}
// Nothing here reads a byte of file content; `blob` is the File itself and is read later, by range, inside a Worker.

const isArchiveName = (name) => /\.mpq$/i.test(name);
const entryOf = (name, file) => ({ name, size: file.size, lastModified: file.lastModified, blob: file });

/** @param {FileSystemDirectoryHandle} root the folder the visitor picked */
export async function scanHandle(root) {
    let dataHandle = null;
    let exeHandle = null;
    for await (const [name, handle] of root.entries()) {
        const lower = name.toLowerCase();
        if (handle.kind === 'directory' && lower === 'data') {
            dataHandle = handle;
        } else if (handle.kind === 'file' && lower === 'wow.exe') {
            exeHandle = handle;
        }
    }

    const archives = [];
    const dataFolders = [];
    if (dataHandle !== null) {
        const files = [];
        for await (const [name, handle] of dataHandle.entries()) {
            if (handle.kind === 'directory') {
                dataFolders.push(name);
            } else if (handle.kind === 'file' && isArchiveName(name)) {
                files.push([name, handle]);
            }
        }
        for (const [name, handle] of files) {
            archives.push(entryOf(name, await handle.getFile()));
        }
    }
    const exe = exeHandle === null ? null : entryOf(exeHandle.name, await exeHandle.getFile());
    return { name: root.name, kind: 'handle', hasData: dataHandle !== null, archives, dataFolders, exe };
}

/** @param {ArrayLike<File>} list an `<input webkitdirectory>` FileList (or any array of Files with webkitRelativePath) */
export async function scanFileList(list) {
    const archives = [];
    const dataFolders = new Set();
    let exe = null;
    let hasData = false;
    let name = '';
    for (let i = 0; i < list.length; i++) {
        const file = list[i];
        const segments = String(file.webkitRelativePath ?? '').split('/');
        if (i === 0) {
            name = segments.length > 1 ? segments[0] : '';
        }
        // segments[0] is the picked folder itself; the rest is the path inside it.
        const rest = segments.slice(1);
        if (rest.length === 1 && rest[0].toLowerCase() === 'wow.exe') {
            exe = entryOf(rest[0], file);
        } else if (rest.length >= 2 && rest[0].toLowerCase() === 'data') {
            hasData = true;
            if (rest.length === 2 && isArchiveName(rest[1])) {
                archives.push(entryOf(rest[1], file));
            } else if (rest.length >= 3) {
                dataFolders.add(rest[1]);
            }
        }
    }
    return { name, kind: 'filelist', hasData, archives, dataFolders: [...dataFolders], exe };
}
