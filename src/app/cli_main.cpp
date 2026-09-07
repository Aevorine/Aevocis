#include "aevocis/platform/windows/command_pipe.hpp"

#include <iostream>
#include <string>

int run(int argc, char** argv) {
    if (argc < 2) {
        std::cerr << "usage: aevocis-cli --status|--show|--theme|--inject <text>\n";
        return 2;
    }
    std::string command;
    if (std::string(argv[1]) == "--status") {
        command = "status";
    } else if (std::string(argv[1]) == "--show") {
        command = "show";
    } else if (std::string(argv[1]) == "--theme") {
        command = "theme";
    } else if (std::string(argv[1]) == "--inject" && argc >= 3) {
        command = "inject:" + std::string(argv[2]);
    } else {
        std::cerr << "unknown command\n";
        return 2;
    }
    const std::string response = aevocis::platform::windows::CommandPipeClient::request(command);
    if (response.empty()) {
        std::cout << "{\"running\":false}\n";
        return command == "status" ? 0 : 1;
    }
    std::cout << response;
    return response.find("\"accepted\":true") != std::string::npos || response.find("\"running\":true") != std::string::npos ? 0 : 1;
}

int main(int argc, char** argv) noexcept {
    try {
        return run(argc, argv);
    } catch (...) {
        return 1;
    }
}
