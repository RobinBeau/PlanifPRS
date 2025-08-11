// Gestion de l'upload des fichiers (Create + Edit)
document.addEventListener('DOMContentLoaded', function () {
    const dropZone = document.getElementById('dropZone');
    const fileInput = document.getElementById('fileUpload');
    // Sur Edit, on sépare les nouveaux fichiers (#newFileList) des existants (#existingFileList)
    const fileList = document.getElementById('newFileList') || document.getElementById('fileList');
    const progressContainer = document.getElementById('uploadProgress');
    const progressBar = document.getElementById('progressBar');

    if (!dropZone || !fileInput || !fileList) return;

    function formatFileSize(bytes) {
        if (bytes === 0) return '0 Bytes';
        const k = 1024;
        const sizes = ['Bytes', 'KB', 'MB', 'GB'];
        const i = Math.floor(Math.log(bytes) / Math.log(k));
        return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
    }

    function getFileIcon(fileName) {
        const extension = (fileName.split('.').pop() || '').toLowerCase();
        const icons = { pdf: 'fas fa-file-pdf', doc: 'fas fa-file-word', docx: 'fas fa-file-word', xls: 'fas fa-file-excel', xlsx: 'fas fa-file-excel', ppt: 'fas fa-file-powerpoint', pptx: 'fas fa-file-powerpoint', txt: 'fas fa-file-alt', csv: 'fas fa-file-csv', jpg: 'fas fa-file-image', jpeg: 'fas fa-file-image', png: 'fas fa-file-image', gif: 'fas fa-file-image', zip: 'fas fa-file-archive', rar: 'fas fa-file-archive', '7z': 'fas fa-file-archive' };
        return icons[extension] || 'fas fa-file';
    }

    function updateFileList(files) {
        // IMPORTANT: ne pas toucher aux fichiers existants (#existingFileList)
        fileList.innerHTML = '';

        Array.from(files).forEach((file) => {
            const fileItem = document.createElement('li');
            fileItem.className = 'file-item';
            fileItem.dataset.new = 'true';

            const fileIcon = document.createElement('i');
            fileIcon.className = `file-icon ${getFileIcon(file.name)}`;

            const fileName = document.createElement('span');
            fileName.className = 'file-name';
            fileName.textContent = file.name;

            const fileSize = document.createElement('span');
            fileSize.className = 'file-size';
            fileSize.textContent = `(${formatFileSize(file.size)})`;

            fileItem.appendChild(fileIcon);
            fileItem.appendChild(fileName);
            fileItem.appendChild(fileSize);

            fileList.appendChild(fileItem);
        });

        console.log('[UPLOAD] Nouveaux fichiers sélectionnés:', Array.from(files).map(f => f.name));
    }

    fileInput.addEventListener('change', function () {
        updateFileList(this.files);

        progressContainer.style.display = 'block';
        progressBar.style.width = '0%';

        let progress = 0;
        const interval = setInterval(() => {
            progress += 5;
            progressBar.style.width = `${Math.min(progress, 100)}%`;

            if (progress >= 100) {
                clearInterval(interval);
                setTimeout(() => { progressContainer.style.display = 'none'; }, 500);
            }
        }, 50);
    });

    ['dragenter', 'dragover', 'dragleave', 'drop'].forEach(eventName => {
        dropZone.addEventListener(eventName, (e) => { e.preventDefault(); e.stopPropagation(); }, false);
    });

    dropZone.addEventListener('dragover', () => { dropZone.classList.add('dragover'); });
    dropZone.addEventListener('dragleave', () => { dropZone.classList.remove('dragover'); });
    dropZone.addEventListener('drop', (e) => {
        dropZone.classList.remove('dragover');
        const files = e.dataTransfer.files;
        if (files.length > 0) {
            fileInput.files = files;
            updateFileList(files);
        }
    });
});