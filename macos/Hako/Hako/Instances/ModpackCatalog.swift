import Foundation
import Observation

@MainActor @Observable final class ModpackCatalog {
    struct SearchKey: Equatable {
        let query: String
        let minecraft: String?
        let sort: ModrinthSort
    }
    var query = ""
    var minecraft: String?
    var sort = ModrinthSort.downloads
    private(set) var projects: [ModrinthProject] = []
    private(set) var loading = false
    private(set) var loadingMore = false
    private(set) var error: String?
    private(set) var moreError: String?
    private(set) var nextOffset = 0
    private var total = 0
    private var generation = UUID()
    private var searchedQuery = ""
    let client: ModrinthClient
    var key: SearchKey { .init(query: query, minecraft: minecraft, sort: sort) }
    var hasMore: Bool { nextOffset < total }

    init(client: ModrinthClient) { self.client = client }

    func search(force: Bool = false) async {
        let key = key
        let token = UUID()
        generation = token
        loading = true; loadingMore = false; error = nil; moreError = nil
        projects = []; nextOffset = 0; total = 0
        do {
            if query != searchedQuery && !force { try await Task.sleep(for: .milliseconds(300)) }
            let page = try await client.searchModpacks(key.query, minecraft: key.minecraft, sort: key.sort, offset: 0)
            guard !Task.isCancelled, generation == token, self.key == key else { return }
            accept(page)
            searchedQuery = key.query
            loading = false
        } catch {
            guard !Task.isCancelled, generation == token, self.key == key else { return }
            self.error = error.localizedDescription
            loading = false
        }
    }

    func loadMore() async {
        guard !loading, !loadingMore, hasMore else { return }
        let key = key, token = generation
        let offset = nextOffset
        loadingMore = true; moreError = nil
        defer { if generation == token, self.key == key { loadingMore = false } }
        do {
            let page = try await client.searchModpacks(key.query, minecraft: key.minecraft, sort: key.sort, offset: offset)
            guard !Task.isCancelled, generation == token, self.key == key else { return }
            accept(page)
        } catch {
            guard !Task.isCancelled, generation == token, self.key == key else { return }
            moreError = error.localizedDescription
        }
    }

    private func accept(_ page: ModrinthSearchPage) {
        var known = Set(projects.map(\.id))
        projects += page.hits.filter { $0.projectType == "modpack" && known.insert($0.id).inserted }
        // Offset follows raw API results, including duplicates and filtered projects.
        nextOffset += page.hits.count
        total = page.hits.isEmpty ? nextOffset : page.total
    }
}
