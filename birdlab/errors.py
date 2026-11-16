class BirdLabError(Exception):
    pass


class InputError(BirdLabError):
    pass


class ContentError(InputError):
    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code


class ModelError(BirdLabError):
    pass


class StorageError(BirdLabError):
    pass
