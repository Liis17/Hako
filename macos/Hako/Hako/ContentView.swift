//
//  ContentView.swift
//  Hako
//
//  Created by Li_is on 04/10/2026.
//

import SwiftUI

struct ContentView: View {
    var body: some View {
        ZStack {
            SakuraBackground()
            WelcomeView(onStart: {})
        }
        .frame(minWidth: 960, minHeight: 540)
        .preferredColorScheme(.light)
    }
}

#Preview {
    ContentView()
        .frame(width: 1280, height: 720)
}
