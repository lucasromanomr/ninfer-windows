#include "serve/http_transport.h"

#include "serve/request_validation.h"

#include <utility>

namespace ninfer::serve {

nlohmann::json parse_json_body(const httplib::Request& request) {
    try {
        return nlohmann::json::parse(request.body);
    } catch (const std::exception&) { bad_request("request body is not valid JSON"); }
}

bool client_disconnected(const httplib::Request& request) {
    return request.is_connection_alive && !request.is_connection_alive();
}

void prepare_sse_response(httplib::Response& response) {
    response.set_header("Cache-Control", "no-cache");
    response.set_header("X-Accel-Buffering", "no");
}

void write_stream_item(httplib::DataSink& sink, std::atomic<bool>& cancelled,
                       std::string_view item) {
    if (cancelled.load(std::memory_order_acquire) || !sink.write(item.data(), item.size())) {
        cancelled.store(true, std::memory_order_release);
        throw ClientDisconnected();
    }
}

void write_stream_items(httplib::DataSink& sink, std::atomic<bool>& cancelled,
                        const std::vector<std::string>& items) {
    for (const std::string& item : items) { write_stream_item(sink, cancelled, item); }
}

void set_owned_json_content(httplib::Response& response, std::string body,
                            std::shared_ptr<RequestLifetime> lifetime) {
    response.set_content(std::move(body), "application/json");
    response.hold_resource(std::move(lifetime));
}

} // namespace ninfer::serve
