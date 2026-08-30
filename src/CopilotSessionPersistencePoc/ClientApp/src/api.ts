export async function apiRequest<T>(url: string, options?: RequestInit): Promise<T> {
  const response = await fetch(url, {
    ...options,
    headers: {
      'Content-Type': 'application/json',
      ...options?.headers,
    },
  })

  if (!response.ok) {
    const problem = await response.json().catch(() => null)
    throw new Error(problem?.detail ?? problem?.title ?? `Request failed (${response.status})`)
  }

  if (response.status === 204) {
    return undefined as T
  }

  return response.json() as Promise<T>
}

export async function uploadArtifact<T>(
  sessionId: string,
  file: File,
): Promise<T> {
  const response = await fetch(
    `/api/sessions/${encodeURIComponent(sessionId)}/artifacts?fileName=${encodeURIComponent(file.name)}`,
    {
      method: 'POST',
      headers: {
        'Content-Type': file.type || 'application/octet-stream',
      },
      body: file,
    },
  )

  if (!response.ok) {
    const problem = await response.json().catch(() => null)
    throw new Error(problem?.detail ?? problem?.title ?? `Upload failed (${response.status})`)
  }

  return response.json() as Promise<T>
}

export async function downloadArtifact(
  sessionId: string,
  artifactId: string,
  fileName: string,
): Promise<void> {
  const response = await fetch(
    `/api/sessions/${encodeURIComponent(sessionId)}/artifacts/${encodeURIComponent(artifactId)}/${encodeURIComponent(fileName)}`,
    { cache: 'no-store' },
  )

  if (!response.ok) {
    const problem = await response.json().catch(() => null)
    throw new Error(problem?.detail ?? problem?.title ?? `Download failed (${response.status})`)
  }

  // Easy Auth answers an expired session with a redirect to the sign-in page, which would
  // otherwise be saved as a corrupt artifact instead of the requested bytes.
  if (response.redirected || new URL(response.url, location.href).origin !== location.origin) {
    throw new Error('Your sign-in expired. Reload the page and try the download again.')
  }

  const objectUrl = URL.createObjectURL(await response.blob())
  const link = document.createElement('a')
  link.href = objectUrl
  link.download = fileName
  link.style.display = 'none'
  document.body.appendChild(link)

  link.click()
  link.remove()
  setTimeout(() => URL.revokeObjectURL(objectUrl), 0)
}
